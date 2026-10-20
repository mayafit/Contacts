/**
 * @fileoverview Chat panel for the contacts assistant (Gemini)
 * @module Contacts/components/assistant/AssistantPanel
 */

import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  IconButton,
  InputBase,
  Link,
  Paper,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  AutoAwesome,
  Close,
  Send,
  Search,
  Public,
  Person,
  TableRows,
  ErrorOutline,
  Insights as InsightsIcon,
  AddComment,
} from '@mui/icons-material';
import type { Contact, ContactGroup } from '../../types/Contact';
import type { GeminiContent } from '../../../../services/api/BackendApiClient';
import { runAgent, type ToolStep } from '../../assistant/agent';
import {
  type AssistantContext,
  type ContactOffer,
  type Proposal,
  type SearchMemory,
} from '../../assistant/tools';
import { mailtoHref, smsHref, telHref, whatsappHref } from '../../device/device';
import { applyProposal, type ApplyResult } from '../../assistant/apply';
import { proposalName } from '../../assistant/changes';
import { useAppDispatch } from '../../types/hooks';
import { getDisplayName } from '../../model/contactView';
import { tokens } from '../../theme/contactsTheme';
import Markdown from './Markdown';

/** Props for AssistantPanel */
export interface AssistantPanelProps {
  /** Whether the panel is shown (it stays mounted to keep the conversation) */
  open: boolean;
  /** Close request */
  onClose: () => void;
  /** Everything the tools need, except propose (added here) */
  context: Omit<AssistantContext, 'propose'>;
  /** Contacts as they are now, by resource name (checked when applying) */
  entities: Record<string, Contact | undefined>;
  /** Labels by resource name */
  groups: Record<string, ContactGroup>;
  /** Model name, shown in the header */
  model?: string;
}

type ProposalStatus = 'pending' | 'applying' | 'applied' | 'discarded' | 'failed';

type ChatItem =
  | { kind: 'user'; text: string }
  | { kind: 'assistant'; text: string }
  | { kind: 'step'; step: ToolStep }
  | {
      kind: 'proposal';
      proposal: Proposal;
      status: ProposalStatus;
      result?: ApplyResult;
      error?: string;
    }
  | { kind: 'contact'; offer: ContactOffer }
  | { kind: 'error'; text: string };

const SUGGESTIONS = [
  'Summarize my address book',
  'Which Hebrew-named contacts have no phonetic name? Propose phonetic names for the first 10',
  'Who have I not been in touch with for over a year but used to email a lot?',
  'Find contacts who work at the same company and suggest a label for each company',
  'Who are my 10 most frequent contacts this year?',
];

/** Short description of a tool call for the chat */
const stepLabel = (s: ToolStep): { icon: React.ReactNode; text: string } => {
  const r = s.result;
  if (r.error) {
    return { icon: <ErrorOutline sx={{ fontSize: 14 }} />, text: `${s.name}: ${String(r.error)}` };
  }
  switch (s.name) {
    case 'find_person': {
      const mail = Array.isArray(r.mailAndCalendar) ? r.mailAndCalendar.length : null;
      const found = Array.isArray(r.contacts) ? r.contacts.length : 0;
      return {
        icon: <Search sx={{ fontSize: 14 }} />,
        text: `Looked for “${String((s.args.names as string[] | undefined)?.[0] ?? '')}” · ${found} in contacts${mail === null ? '' : ` · ${mail} in mail & calendar`}`,
      };
    }
    case 'search_contacts':
      return {
        icon: <Search sx={{ fontSize: 14 }} />,
        text: `Searched contacts · ${String(r.total)} found`,
      };
    case 'get_contact':
      return {
        icon: <Person sx={{ fontSize: 14 }} />,
        text: `Read ${String(r.name ?? 'a contact')}`,
      };
    case 'web_search':
      return {
        icon: <Public sx={{ fontSize: 14 }} />,
        text: `Searched the web: “${String(s.args.query ?? '')}”`,
      };
    case 'show_contacts':
      return {
        icon: <TableRows sx={{ fontSize: 14 }} />,
        text: `Showing ${String(r.contacts)} contacts in the table`,
      };
    case 'open_contact':
      return { icon: <Person sx={{ fontSize: 14 }} />, text: 'Opened a contact card' };
    case 'open_merge':
      return { icon: <Person sx={{ fontSize: 14 }} />, text: 'Opened the merge dialog' };
    default:
      return { icon: <InsightsIcon sx={{ fontSize: 14 }} />, text: s.name.replace(/_/g, ' ') };
  }
};

/** A drafted call / message / email with the button that opens the right app */
const ContactOfferCard: React.FC<{ offer: ContactOffer }> = ({ offer }) => {
  const { contact, channel, message, subject } = offer;
  const phone = contact.phoneNumbers?.find((p) => p.value);
  const email = contact.emailAddresses?.find((e) => e.value);
  const href =
    channel === 'call'
      ? telHref(phone?.value ?? '')
      : channel === 'sms'
        ? smsHref(phone?.value ?? '', message)
        : channel === 'whatsapp'
          ? whatsappHref(phone?.value ?? '', phone?.canonicalForm, message)
          : mailtoHref(email?.value ?? '', subject, message);
  const label = { call: 'Call', sms: 'Text', whatsapp: 'WhatsApp', email: 'Email' }[channel];
  return (
    <Paper variant="outlined" sx={{ p: 1.5, borderColor: tokens.accent, bgcolor: tokens.surface }}>
      <Typography variant="caption" sx={{ color: tokens.textMuted }}>
        {label} · {channel === 'email' ? email?.value : phone?.value}
      </Typography>
      <Typography variant="body2" dir="auto" sx={{ fontWeight: 600, mb: message ? 0.5 : 1 }}>
        {getDisplayName(contact)}
      </Typography>
      {subject && (
        <Typography
          variant="caption"
          dir="auto"
          sx={{ display: 'block', color: tokens.textSecondary }}
        >
          {subject}
        </Typography>
      )}
      {message && (
        <Typography
          variant="body2"
          dir="auto"
          sx={{ whiteSpace: 'pre-wrap', color: tokens.textSecondary, mb: 1 }}
        >
          {message}
        </Typography>
      )}
      <Button
        size="small"
        variant="contained"
        href={href || '#'}
        disabled={!href}
        target={href.startsWith('https:') ? '_blank' : undefined}
        rel="noopener noreferrer"
      >
        {label} {getDisplayName(contact)}
      </Button>
    </Paper>
  );
};

/** A proposal: what would change, with Apply / Discard */
const ProposalCard: React.FC<{
  item: Extract<ChatItem, { kind: 'proposal' }>;
  onApply: () => void;
  onDiscard: () => void;
  onShow: (ids: string[], title: string) => void;
}> = ({ item, onApply, onDiscard, onShow }) => {
  const [expanded, setExpanded] = useState(false);
  const p = item.proposal;
  const rows: { name: string; lines: { field: string; from: string; to: string }[] }[] =
    p.kind === 'update'
      ? p.items.map((i) => ({ name: proposalName(i.base), lines: i.diff }))
      : p.kind === 'create'
        ? [{ name: 'New contact', lines: p.diff }]
        : p.contacts.map((c) => ({ name: getDisplayName(c), lines: [] }));
  const shown = expanded ? rows : rows.slice(0, p.kind === 'update' ? 5 : 10);
  const count = p.kind === 'update' ? p.items.length : p.kind === 'create' ? 1 : p.contacts.length;
  const headline =
    p.kind === 'labels'
      ? `${p.add ? 'Add' : 'Remove'} ${count} ${count === 1 ? 'contact' : 'contacts'} ${p.add ? 'to' : 'from'} label “${p.label}”`
      : p.kind === 'trash'
        ? `Move ${count} ${count === 1 ? 'contact' : 'contacts'} to Trash`
        : p.kind === 'create'
          ? 'Create a contact'
          : `Edit ${count} ${count === 1 ? 'contact' : 'contacts'}`;

  return (
    <Paper
      variant="outlined"
      sx={{
        p: 1.5,
        borderColor: item.status === 'pending' ? tokens.accent : tokens.border,
        bgcolor: tokens.surface,
      }}
    >
      <Typography variant="caption" sx={{ color: tokens.textMuted }}>
        Proposal {p.id} · {headline}
      </Typography>
      <Typography variant="body2" dir="auto" sx={{ fontWeight: 600, mb: 1 }}>
        {p.summary}
      </Typography>
      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.75 }}>
        {shown.map((r, i) => (
          <Box key={i}>
            <Typography variant="body2" dir="auto" sx={{ fontWeight: r.lines.length ? 600 : 400 }}>
              {r.name}
            </Typography>
            {r.lines.map((l, li) => (
              <Typography
                key={li}
                variant="caption"
                dir="auto"
                sx={{ display: 'block', color: tokens.textSecondary }}
              >
                {l.field}:{' '}
                {l.from && (
                  <Box
                    component="span"
                    sx={{ textDecoration: 'line-through', color: tokens.textMuted }}
                  >
                    {l.from}
                  </Box>
                )}
                {l.from && l.to && ' → '}
                {l.to && (
                  <Box component="span" sx={{ color: tokens.text }}>
                    {l.to}
                  </Box>
                )}
                {!l.to && ' (removed)'}
              </Typography>
            ))}
          </Box>
        ))}
        {rows.length > shown.length && (
          <Link
            component="button"
            type="button"
            onClick={() => setExpanded(true)}
            sx={{ alignSelf: 'flex-start', fontSize: 13 }}
          >
            and {rows.length - shown.length} more
          </Link>
        )}
      </Box>
      {p.warnings.length > 0 && (
        <Typography variant="caption" sx={{ display: 'block', color: tokens.attention, mt: 1 }}>
          {p.warnings.slice(0, 5).join(' · ')}
          {p.warnings.length > 5 ? ` · +${p.warnings.length - 5} more` : ''}
        </Typography>
      )}
      <Box sx={{ display: 'flex', gap: 1, mt: 1.25, alignItems: 'center', flexWrap: 'wrap' }}>
        {item.status === 'pending' && (
          <>
            <Button size="small" variant="contained" onClick={onApply}>
              Apply
            </Button>
            <Button size="small" color="inherit" onClick={onDiscard}>
              Discard
            </Button>
          </>
        )}
        {item.status === 'applying' && <CircularProgress size={18} />}
        {item.status === 'applied' && item.result && (
          <Typography variant="caption" sx={{ color: 'success.main' }}>
            Applied · {item.result.done} done
            {item.result.failed.length ? ` · ${item.result.failed.length} not applied` : ''}
          </Typography>
        )}
        {item.status === 'discarded' && (
          <Typography variant="caption" sx={{ color: tokens.textMuted }}>
            Discarded
          </Typography>
        )}
        {item.status === 'failed' && (
          <Typography variant="caption" sx={{ color: 'error.main' }}>
            {item.error}
          </Typography>
        )}
        <Box sx={{ flex: 1 }} />
        {(p.kind === 'update' || p.kind === 'labels' || p.kind === 'trash') && (
          <Button
            size="small"
            color="inherit"
            onClick={() =>
              onShow(
                p.kind === 'update'
                  ? p.items.map((i) => i.base.resourceName)
                  : p.contacts.map((c) => c.resourceName),
                `Proposal ${p.id}`,
              )
            }
          >
            Show in table
          </Button>
        )}
      </Box>
      {item.result?.failed.length ? (
        <Box sx={{ mt: 0.5 }}>
          {item.result.failed.slice(0, 6).map((f, i) => (
            <Typography
              key={i}
              variant="caption"
              dir="auto"
              sx={{ display: 'block', color: 'error.main' }}
            >
              {f.name}: {f.message}
            </Typography>
          ))}
        </Box>
      ) : null}
    </Paper>
  );
};

/**
 * Assistant chat panel.
 * @param props - Component props
 * @returns Panel element
 */
const AssistantPanel: React.FC<AssistantPanelProps> = ({
  open,
  onClose,
  context,
  entities,
  groups,
  model,
}) => {
  const dispatch = useAppDispatch();
  const [items, setItems] = useState<ChatItem[]>([]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const history = useRef<GeminiContent[]>([]);
  const memory = useRef<SearchMemory>(new Map());
  const notes = useRef<string[]>([]);
  // Proposals being applied: a double click must not apply (or create) twice
  const applying = useRef(new Set<string>());
  const latest = useRef({ entities, groups });
  latest.current = { entities, groups };
  const endRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    endRef.current?.scrollIntoView({ block: 'end' });
  }, [items]);

  useEffect(() => {
    if (open) {
      inputRef.current?.focus();
    }
  }, [open]);

  const add = (item: ChatItem) => setItems((list) => [...list, item]);

  const send = useCallback(
    async (text: string) => {
      const message = text.trim();
      if (!message || busy) {
        return;
      }
      setInput('');
      add({ kind: 'user', text: message });
      setBusy(true);
      const pending = notes.current.splice(0);
      const parts = [
        {
          text: pending.length
            ? `[Since my last message: ${pending.join('; ')}]\n\n${message}`
            : message,
        },
      ];
      try {
        const result = await runAgent(
          history.current,
          parts,
          {
            ...context,
            propose: (proposal) => add({ kind: 'proposal', proposal, status: 'pending' }),
            offerContact: (offer) => add({ kind: 'contact', offer }),
          },
          memory.current,
          (step) => {
            if (!step.name.startsWith('propose_') || step.result.error) {
              add({ kind: 'step', step });
            }
          },
        );
        history.current = result.history;
        add({ kind: 'assistant', text: result.text });
      } catch (e) {
        add({ kind: 'error', text: (e as Error).message || 'The assistant failed' });
      } finally {
        setBusy(false);
      }
    },
    [busy, context],
  );

  const setProposal = (id: string, patch: Partial<Extract<ChatItem, { kind: 'proposal' }>>) =>
    setItems((list) =>
      list.map((it) =>
        it.kind === 'proposal' && it.proposal.id === id ? { ...it, ...patch } : it,
      ),
    );

  const apply = async (proposal: Proposal) => {
    if (applying.current.has(proposal.id)) {
      return;
    }
    applying.current.add(proposal.id);
    setProposal(proposal.id, { status: 'applying' });
    try {
      const result = await applyProposal(
        proposal,
        dispatch,
        latest.current.entities,
        latest.current.groups,
      );
      setProposal(proposal.id, { status: 'applied', result });
      notes.current.push(
        `I applied proposal ${proposal.id} (${result.done} done${result.failed.length ? `, ${result.failed.length} failed: ${result.failed.map((f) => `${f.name} – ${f.message}`).join(', ')}` : ''})`,
      );
    } catch (e) {
      const message = (e as Error).message || 'Failed';
      setProposal(proposal.id, { status: 'failed', error: message });
      notes.current.push(`Applying proposal ${proposal.id} failed: ${message}`);
    }
  };

  const discard = (proposal: Proposal) => {
    setProposal(proposal.id, { status: 'discarded' });
    notes.current.push(`I discarded proposal ${proposal.id}`);
  };

  const reset = () => {
    setItems([]);
    history.current = [];
    memory.current = new Map();
    notes.current = [];
  };

  const pendingCount = items.filter((i) => i.kind === 'proposal' && i.status === 'pending').length;

  return (
    <Box
      component="aside"
      aria-label="Assistant"
      sx={{
        display: open ? 'flex' : 'none',
        flexDirection: 'column',
        width: { xs: '100%', md: 420 },
        flexShrink: 0,
        borderLeft: `1px solid ${tokens.border}`,
        bgcolor: tokens.surface,
        position: { xs: 'fixed', md: 'static' },
        top: { xs: 60, md: 'auto' },
        bottom: { xs: 0, md: 'auto' },
        right: 0,
        zIndex: { xs: 1200, md: 'auto' },
        minHeight: 0,
      }}
    >
      <Box
        sx={{
          display: 'flex',
          alignItems: 'center',
          gap: 1,
          px: 2,
          height: 52,
          borderBottom: `1px solid ${tokens.border}`,
        }}
      >
        <AutoAwesome sx={{ fontSize: 18, color: tokens.accent }} />
        <Typography variant="subtitle1">Assistant</Typography>
        {model && (
          <Typography variant="caption" sx={{ color: tokens.textMuted }}>
            {model}
          </Typography>
        )}
        <Box sx={{ flex: 1 }} />
        <Tooltip title="New conversation">
          <span>
            <IconButton
              size="small"
              onClick={reset}
              disabled={busy || items.length === 0}
              aria-label="New conversation"
            >
              <AddComment fontSize="small" />
            </IconButton>
          </span>
        </Tooltip>
        <IconButton size="small" onClick={onClose} aria-label="Close assistant">
          <Close fontSize="small" />
        </IconButton>
      </Box>

      <Box
        sx={{
          flex: 1,
          overflowY: 'auto',
          p: 2,
          display: 'flex',
          flexDirection: 'column',
          gap: 1.5,
        }}
        aria-live="polite"
      >
        {items.length === 0 && (
          <Box>
            <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 1.5 }}>
              Ask about your contacts, or ask for changes. Changes are proposed first and saved only
              when you click Apply.
            </Typography>
            <Box
              sx={{ display: 'flex', flexDirection: 'column', gap: 1, alignItems: 'flex-start' }}
            >
              {SUGGESTIONS.map((s) => (
                <Chip
                  key={s}
                  label={s}
                  onClick={() => send(s)}
                  variant="outlined"
                  sx={{
                    height: 'auto',
                    '& .MuiChip-label': { whiteSpace: 'normal', py: 0.75 },
                    borderColor: tokens.borderStrong,
                  }}
                />
              ))}
            </Box>
          </Box>
        )}
        {items.map((item, i) => {
          if (item.kind === 'user') {
            return (
              <Box
                key={i}
                dir="auto"
                sx={{
                  alignSelf: 'flex-end',
                  maxWidth: '85%',
                  px: 1.5,
                  py: 1,
                  borderRadius: '12px 12px 2px 12px',
                  bgcolor: 'rgba(57,135,229,0.16)',
                  whiteSpace: 'pre-wrap',
                  fontSize: 14,
                }}
              >
                {item.text}
              </Box>
            );
          }
          if (item.kind === 'assistant') {
            return <Markdown key={i} text={item.text} onContact={context.openContact} />;
          }
          if (item.kind === 'step') {
            const { icon, text } = stepLabel(item.step);
            const sources =
              item.step.name === 'web_search'
                ? (item.step.result.sources as { title?: string; uri?: string }[] | undefined)
                : undefined;
            return (
              <Box key={i}>
                <Chip
                  size="small"
                  icon={icon as React.ReactElement}
                  label={text}
                  color={item.step.result.error ? 'warning' : 'default'}
                  variant="outlined"
                  sx={{ maxWidth: '100%', borderColor: tokens.border, color: tokens.textSecondary }}
                />
                {sources && sources.length > 0 && (
                  <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap', mt: 0.5, pl: 1 }}>
                    {sources.slice(0, 5).map((src, si) => (
                      <Link
                        key={si}
                        href={src.uri}
                        target="_blank"
                        rel="noopener noreferrer"
                        sx={{ fontSize: 12 }}
                      >
                        {src.title || 'source'}
                      </Link>
                    ))}
                  </Box>
                )}
              </Box>
            );
          }
          if (item.kind === 'contact') {
            return <ContactOfferCard key={i} offer={item.offer} />;
          }
          if (item.kind === 'proposal') {
            return (
              <ProposalCard
                key={i}
                item={item}
                onApply={() => apply(item.proposal)}
                onDiscard={() => discard(item.proposal)}
                onShow={context.showContacts}
              />
            );
          }
          return (
            <Alert key={i} severity="error">
              {item.text}
            </Alert>
          );
        })}
        {busy && (
          <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', color: tokens.textMuted }}>
            <CircularProgress size={16} />
            <Typography variant="caption">Thinking…</Typography>
          </Box>
        )}
        <div ref={endRef} />
      </Box>

      <Box sx={{ borderTop: `1px solid ${tokens.border}`, p: 1.5 }}>
        {pendingCount > 0 && (
          <Typography variant="caption" sx={{ display: 'block', color: tokens.attention, mb: 0.5 }}>
            {pendingCount} {pendingCount === 1 ? 'proposal waits' : 'proposals wait'} for Apply or
            Discard
          </Typography>
        )}
        <Box
          sx={{
            display: 'flex',
            alignItems: 'flex-end',
            gap: 1,
            px: 1.5,
            py: 1,
            borderRadius: '10px',
            border: `1px solid ${tokens.border}`,
            bgcolor: tokens.page,
            '&:focus-within': { borderColor: tokens.accent },
          }}
        >
          <InputBase
            inputRef={inputRef}
            multiline
            maxRows={6}
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                void send(input);
              }
            }}
            placeholder="Ask about your contacts…"
            inputProps={{ 'aria-label': 'Message the assistant', dir: 'auto' }}
            sx={{ flex: 1, fontSize: 14 }}
          />
          <IconButton
            size="small"
            onClick={() => send(input)}
            disabled={busy || !input.trim()}
            aria-label="Send"
          >
            <Send fontSize="small" />
          </IconButton>
        </Box>
        <Typography variant="caption" sx={{ display: 'block', color: tokens.textMuted, mt: 0.75 }}>
          Messages and the contact details the assistant looks up are sent to Google Gemini.
        </Typography>
      </Box>
    </Box>
  );
};

export default AssistantPanel;
