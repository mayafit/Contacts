/**
 * @fileoverview The assistant's agent loop: Gemini (through the backend) decides, the
 * browser runs the tools, until Gemini answers in text
 * @module Contacts/assistant/agent
 */

import {
  backendApiClient,
  type GeminiContent,
  type GeminiPart,
} from '../../../services/api/BackendApiClient';
import { executeTool, TOOL_DECLARATIONS, type AssistantContext, type SearchMemory } from './tools';

/** Tool rounds per user message before the loop gives up */
const MAX_STEPS = 10;

/** Conversation size (JSON chars) above which the oldest turns are dropped */
const MAX_HISTORY_CHARS = 300_000;

/** A tool call made while answering, for the chat to show */
export interface ToolStep {
  name: string;
  args: Record<string, unknown>;
  result: Record<string, unknown>;
}

/**
 * Instructions for the model.
 * @param ctx - Assistant context
 * @returns System prompt
 */
export const systemPrompt = (ctx: AssistantContext): string => {
  const labels = Object.values(ctx.groups)
    .filter((g) => g.groupType === 'USER_CONTACT_GROUP')
    .map((g) => g.name)
    .slice(0, 150);
  return [
    "You are the assistant inside the user's Google Contacts manager.",
    `Today is ${new Date().toISOString().slice(0, 10)}. The user is ${ctx.user.name ?? 'the account owner'}${ctx.user.email ? ` <${ctx.user.email}>` : ''}.`,
    `The address book has ${ctx.contacts.length} contacts (${ctx.trashed.length} more in the app's Trash).`,
    `Labels: ${labels.length ? labels.join(', ') : 'none'}.`,
    ctx.activityReady
      ? 'Email and meeting activity (Gmail senders/recipients and Calendar meetings of the last year) is available through the tools.'
      : 'Email and meeting activity is NOT connected; if asked, say it can be connected on the Insights tab.',
    '',
    'Rules:',
    '- Answer from tool results, never from memory or guesses. Search before saying something does not exist.',
    '- You cannot save anything yourself. Tools named propose_* only create a proposal the user reviews and applies; say "I proposed…", never "I changed…".',
    '- Batch related edits into one propose_changes call. For many contacts and one label use propose_label with the searchId.',
    '- Phonetic names: Latin-letter pronunciation of Hebrew names (e.g. דנה כהן → Dana Cohen). Propose them only when you are confident.',
    '- Web search results may be about a different person with the same name. Say where information came from before proposing changes based on it, and prefer asking the user.',
    '- Refer to a contact as a link [Name](contact:ID) using its id, so the user can open it.',
    '- Reply in the language the user writes in (Hebrew or English). Be brief; use short lists. Do not dump long JSON.',
    '- When results are cut off (returned < total), say how many there are and offer to show them all with show_contacts.',
  ].join('\n');
};

/**
 * Drops the oldest exchanges while the conversation is too big. Cuts only before
 * a user text message, so a tool call is never separated from its result.
 * @param history - Conversation
 * @returns Trimmed conversation
 */
export const trimHistory = (history: GeminiContent[]): GeminiContent[] => {
  let h = history;
  while (JSON.stringify(h).length > MAX_HISTORY_CHARS) {
    const next = h.findIndex(
      (c, i) => i > 0 && c.role === 'user' && c.parts.some((p) => p.text !== undefined),
    );
    if (next <= 0) {
      break;
    }
    h = h.slice(next);
  }
  return h;
};

/**
 * Answers one user message.
 * @param history - Conversation so far (not modified)
 * @param userParts - The new user message
 * @param ctx - Assistant context
 * @param memory - Search results remembered across the conversation
 * @param onStep - Called after each tool call
 * @returns New conversation and the answer text
 */
export const runAgent = async (
  history: GeminiContent[],
  userParts: GeminiPart[],
  ctx: AssistantContext,
  memory: SearchMemory,
  onStep: (step: ToolStep) => void,
): Promise<{ history: GeminiContent[]; text: string }> => {
  let contents = trimHistory([...history, { role: 'user', parts: userParts }]);

  for (let step = 0; step < MAX_STEPS; step += 1) {
    const response = await backendApiClient.assistantGenerate({
      systemInstruction: { parts: [{ text: systemPrompt(ctx) }] },
      contents,
      tools: [{ functionDeclarations: TOOL_DECLARATIONS }],
    });
    const candidate = response.candidates?.[0];
    if (!candidate?.content?.parts?.length) {
      const why = response.promptFeedback?.blockReason ?? candidate?.finishReason ?? 'no answer';
      throw new Error(`Gemini returned nothing (${why}).`);
    }

    // Keep the model's message exactly as sent (thought signatures included)
    contents = [...contents, { role: 'model', parts: candidate.content.parts }];
    const calls = candidate.content.parts.filter((p) => p.functionCall);
    if (calls.length === 0) {
      const text = candidate.content.parts
        .filter((p) => p.text && !p.thought)
        .map((p) => p.text)
        .join('')
        .trim();
      return { history: contents, text: text || '(no answer)' };
    }

    const responses: GeminiPart[] = [];
    for (const part of calls) {
      const call = part.functionCall!;
      const args = (call.args ?? {}) as Record<string, unknown>;
      let result: Record<string, unknown>;
      try {
        result = await executeTool(call.name, args, ctx, memory);
      } catch (e) {
        result = { error: (e as Error).message || 'The tool failed' };
      }
      onStep({ name: call.name, args, result });
      responses.push({
        functionResponse: {
          name: call.name,
          response: result,
          ...(call.id ? { id: call.id } : {}),
        },
      });
    }
    contents = [...contents, { role: 'user', parts: responses }];
  }

  return {
    history: contents,
    text: 'I stopped after too many steps. Try a more specific question.',
  };
};
