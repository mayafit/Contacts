/**
 * @fileoverview Backend API Client
 * Handles all communication with the C# ASP.NET backend API
 * Replaces direct Google People API calls with backend endpoints
 * Story: 2.1B - Backend API Integration
 */

import type { Contact, ContactGroup, ContactPatch } from '../../entries/Contacts/types/Contact';
import type {
  AccessTokenInfo,
  ActivityDetail,
  ActivitySummary,
  AtlasQuery,
  AtlasSummary,
  HistogramRow,
  NearResult,
  ParsedData,
  SearchResult,
  SeriesPoint,
} from '../../entries/Contacts/atlas/types';

/** One ingest batch */
export type AtlasIngest = Omit<ParsedData, 'source'> & {
  source: string;
  importId?: string | null;
  fileName?: string | null;
};
import { logger } from '../../shared/logger';

const API_BASE_URL = process.env.REACT_APP_API_BASE_URL || 'http://localhost:5000/api';

/**
 * API response wrapper from backend
 */
interface ApiResponse<T> {
  success: boolean;
  data?: T;
  error?: {
    code: string;
    message: string;
    details?: unknown;
    timestamp: string;
  };
  /** HTTP status of the response (added by the client, not sent by the server) */
  status?: number;
}

/** Window event raised when an API call reports the Google session has ended */
export const SESSION_EXPIRED_EVENT = 'contacts:session-expired';

/**
 * Error thrown for a failed backend call. The message ends with "[HTTP <status>]"
 * so status-based classification (retryable, 412 conflict) works on the text alone.
 */
export class ApiRequestError extends Error {
  /** HTTP status, 0 when the server could not be reached */
  readonly status: number;

  /** Backend error code, e.g. CONTACT_CONFLICT */
  readonly code: string;

  /** Structured details from the server (e.g. what a merge would lose) */
  readonly details?: unknown;

  constructor(message: string, status: number, code: string, details?: unknown) {
    super(status ? `${message} [HTTP ${status}]` : message);
    this.name = 'ApiRequestError';
    this.status = status;
    this.code = code;
    this.details = details;
  }
}

/**
 * Throws ApiRequestError when a response is not a success with data.
 * @param response - Backend response
 * @param fallback - Message when the server sent none
 * @returns The response data
 */
function unwrap<T>(response: ApiResponse<T>, fallback: string): T {
  if (!response.success || response.data === undefined || response.data === null) {
    throw new ApiRequestError(
      response.error?.message || fallback,
      response.status ?? 0,
      response.error?.code || 'UNKNOWN_ERROR',
      response.error?.details,
    );
  }
  return response.data;
}

/**
 * Merge request sent to the backend
 */
/** Activity per email address, from Gmail headers and Calendar */
export interface PersonActivity {
  lastEmailAt?: string | null;
  lastSentAt?: string | null;
  lastReceivedAt?: string | null;
  sentCount: number;
  receivedCount: number;
  /** Emails per month, "yyyy-MM" */
  emailMonths: Record<string, number>;
  lastMeetingAt?: string | null;
  lastMeetingTitle?: string | null;
  nextMeetingAt?: string | null;
  nextMeetingTitle?: string | null;
  /** Past meetings per month, "yyyy-MM" */
  meetingMonths: Record<string, number>;
}

/** Activity snapshot state */
export interface ActivityStatusResponse {
  connected: { gmail: boolean; calendar: boolean; signatures?: boolean };
  status: 'none' | 'running' | 'ready' | 'error';
  progress?: { phase: string; done: number } | null;
  error?: string | null;
  errorCode?: string | null;
  updatedAt?: string | null;
  since?: string | null;
  peopleCount: number;
  /** The name directory (find a person by name in mail/meetings) exists */
  hasDirectory?: boolean;
}

/** Signature reading result for one contact */
export interface SignatureResult {
  /** Details were extracted */
  found: boolean;
  /** Emails read */
  emails: number;
  lastEmailAt?: string;
  details?: import('../../entries/Contacts/assistant/signatures').SignatureDetails | null;
}

/** Someone seen in mail headers or as a meeting guest */
export interface DirectoryMatch {
  email: string;
  names: string[];
  sent: number;
  received: number;
  copiedTogether: number;
  meetings: number;
  lastSeen?: string | null;
  /** Name match strength (suggestions): 3 same name, 2 address spells it, 1 name plus a word */
  score?: number;
}

/** A recent email thread with a person: who wrote last, and when */
export interface ThreadState {
  threadId: string;
  lastAt: string;
  lastFromMe: boolean;
  counterparts: string[];
  subject?: string | null;
  inInbox: boolean;
  messages: number;
}

/** Activity snapshot with the data */
export interface ActivityResponse extends ActivityStatusResponse {
  people: Record<string, PersonActivity>;
  /** Recent threads, newest first */
  threads?: ThreadState[];
}

/** Gemini content part (only the shapes the assistant uses are typed) */
export interface GeminiPart {
  text?: string;
  thought?: boolean;
  thoughtSignature?: string;
  functionCall?: { name: string; args?: Record<string, unknown>; id?: string };
  functionResponse?: { name: string; response: Record<string, unknown>; id?: string };
}

/** Gemini message */
export interface GeminiContent {
  role: 'user' | 'model';
  parts: GeminiPart[];
}

/** Gemini generateContent request */
export interface GeminiRequest {
  systemInstruction?: { parts: GeminiPart[] };
  contents: GeminiContent[];
  tools?: unknown[];
  toolConfig?: unknown;
}

/** Gemini generateContent response */
export interface GeminiResponse {
  candidates?: { content?: GeminiContent; finishReason?: string }[];
  promptFeedback?: { blockReason?: string };
  usageMetadata?: { promptTokenCount?: number; candidatesTokenCount?: number };
}

/** Outcome of a merge */
export interface MergeResult {
  contact: Contact;
  deleted: string[];
  trashLabel?: string | null;
  /**
   * Per merged-away contact, what could not be carried over to the kept one
   * (photo, skills, a different gender); it stays on that contact in Trash
   */
  notCopied?: Record<string, string[]>;
}

export interface MergeRequest {
  /** Contact that is kept */
  primaryResourceName: string;
  /** Etag plus merged groups for the kept contact; null when it needs no change */
  patch: ContactPatch | null;
  /** Contacts folded in and then deleted */
  deleteResourceNames: string[];
  /** Labels to add to the kept contact */
  addLabels: string[];
  /** Move the merged-away contacts to the app's Trash instead of deleting them */
  moveToTrash: boolean;
  /** Contacts whose unmerged data (reported by a previous 409) the user accepted losing */
  acknowledgedResourceNames: string[];
  /** Etags of the contacts to delete as the merge was planned; changed ones are refused */
  deleteEtags: Record<string, string>;
}

/**
 * Contacts list response
 */
interface ContactsResponse {
  contacts: Contact[];
  nextPageToken?: string;
  totalCount: number;
}

/**
 * Authentication status response
 */
interface AuthStatusResponse {
  isAuthenticated: boolean;
  user?: {
    id: string;
    email: string;
    name: string;
    picture?: string;
  };
}

/**
 * Backend API client for communicating with C# ASP.NET API
 */
export class BackendApiClient {
  private readonly baseUrl: string;

  constructor(baseUrl: string = API_BASE_URL) {
    this.baseUrl = baseUrl;
  }

  /**
   * Make authenticated API request to backend
   */
  private async request<T>(endpoint: string, options: RequestInit = {}): Promise<ApiResponse<T>> {
    const url = `${this.baseUrl}${endpoint}`;

    const config: RequestInit = {
      ...options,
      credentials: 'include', // Send cookies with request
      headers: {
        'Content-Type': 'application/json',
        ...options.headers,
      },
    };

    logger.debug(
      {
        context: 'BackendApiClient',
        metadata: { method: config.method || 'GET', url },
      },
      'Making API request',
    );

    try {
      const response = await fetch(url, config);
      // A proxy error page (502/504) is HTML, not JSON; keep the status either way.
      const data = (await response.json().catch(() => ({
        success: false,
        error: {
          code: 'BAD_RESPONSE',
          message: `Unexpected response from server (${response.status})`,
          timestamp: new Date().toISOString(),
        },
      }))) as ApiResponse<T>;
      data.status = response.status;
      // The Google session ended (refresh token expired or revoked). The app
      // listens for this and sends the user to sign in; the status check is
      // excluded because "not signed in" is its normal answer.
      if (response.status === 401 && !endpoint.startsWith('/auth/')) {
        window.dispatchEvent(new CustomEvent(SESSION_EXPIRED_EVENT));
      }

      if (!response.ok) {
        logger.error(
          {
            context: 'BackendApiClient',
            metadata: {
              status: response.status,
              statusText: response.statusText,
              error: data.error,
            },
          },
          'API request failed',
        );

        // Return error response
        return data as ApiResponse<T>;
      }

      logger.debug(
        {
          context: 'BackendApiClient',
          metadata: { url, success: data.success },
        },
        'API request successful',
      );

      return data as ApiResponse<T>;
    } catch (error) {
      logger.error(
        {
          context: 'BackendApiClient',
          metadata: { url, error },
        },
        'Network error during API request',
      );

      throw error;
    }
  }

  // ===================================
  // Authentication Methods
  // ===================================

  /**
   * Redirect to backend OAuth login
   */
  login(): void {
    logger.info({ context: 'BackendApiClient' }, 'Redirecting to backend OAuth login');

    window.location.href = `${this.baseUrl}/auth/login`;
  }

  /**
   * Logout from backend
   */
  async logout(): Promise<void> {
    logger.info({ context: 'BackendApiClient' }, 'Logging out');

    const response = await this.request('/auth/logout', {
      method: 'POST',
    });

    if (!response.success) {
      throw new Error(response.error?.message || 'Logout failed');
    }
  }

  /**
   * Check authentication status
   */
  async getAuthStatus(): Promise<AuthStatusResponse> {
    logger.info({ context: 'BackendApiClient/getAuthStatus' }, 'Checking authentication status');

    const response = await this.request<AuthStatusResponse>('/auth/status');

    logger.info(
      {
        context: 'BackendApiClient/getAuthStatus',
        metadata: {
          success: response.success,
          isAuthenticated: response.data?.isAuthenticated,
          hasUser: !!response.data?.user,
        },
      },
      'Auth status response received',
    );

    if (!response.success || !response.data) {
      throw new Error(response.error?.message || 'Failed to get auth status');
    }

    return response.data;
  }

  /**
   * Refresh access token
   */
  async refreshToken(): Promise<void> {
    logger.debug({ context: 'BackendApiClient' }, 'Refreshing access token');

    const response = await this.request('/auth/refresh', {
      method: 'POST',
    });

    if (!response.success) {
      throw new Error(response.error?.message || 'Token refresh failed');
    }
  }

  // ===================================
  // Contacts Methods
  // ===================================

  /**
   * Fetch all contacts with pagination
   */
  async fetchContacts(
    pageSize: number = 1000,
    pageToken?: string,
  ): Promise<{ contacts: Contact[]; nextPageToken?: string }> {
    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { pageSize, hasPageToken: !!pageToken },
      },
      'Fetching contacts',
    );

    const params = new URLSearchParams({
      pageSize: pageSize.toString(),
    });

    if (pageToken) {
      params.append('pageToken', pageToken);
    }

    const response = await this.request<ContactsResponse>(`/contacts?${params.toString()}`);
    const data = unwrap(response, 'Failed to fetch contacts');

    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { count: data.contacts.length },
      },
      'Contacts fetched successfully',
    );

    return {
      contacts: data.contacts,
      nextPageToken: data.nextPageToken,
    };
  }

  /**
   * Fetch the user's contact groups (labels)
   */
  async fetchContactGroups(): Promise<ContactGroup[]> {
    const response = await this.request<ContactGroup[]>('/contacts/groups');
    return unwrap(response, 'Failed to fetch labels');
  }

  /**
   * Get a single contact by resource name
   */
  async getContact(resourceName: string): Promise<Contact> {
    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { resourceName },
      },
      'Fetching contact',
    );

    const response = await this.request<Contact>(`/contacts/${encodeURIComponent(resourceName)}`);
    return unwrap(response, 'Failed to fetch contact');
  }

  /**
   * Update a contact. Only the groups present in the patch are written; the
   * etag must be the one the contact was last read with.
   * @param resourceName - Contact resource name (e.g., "people/c123")
   * @param patch - Etag plus the changed field groups
   * @returns The full updated contact with its new etag
   */
  async updateContact(resourceName: string, patch: ContactPatch): Promise<Contact> {
    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { resourceName, fields: Object.keys(patch).filter((k) => k !== 'etag') },
      },
      'Updating contact',
    );

    const response = await this.request<Contact>(`/contacts/${encodeURIComponent(resourceName)}`, {
      method: 'PUT',
      body: JSON.stringify(patch),
    });
    const contact = unwrap(response, 'Failed to update contact');

    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { resourceName },
      },
      'Contact updated successfully',
    );

    return contact;
  }

  /**
   * Delete contacts (one request per 500)
   * @param resourceNames - Contacts to delete
   * @returns Number deleted
   */
  async deleteContacts(resourceNames: string[]): Promise<number> {
    logger.warn(
      { context: 'BackendApiClient', metadata: { count: resourceNames.length } },
      'Deleting contacts',
    );
    let deleted = 0;
    for (let i = 0; i < resourceNames.length; i += 500) {
      const response = await this.request<{ deleted: number }>('/contacts/batch-delete', {
        method: 'POST',
        body: JSON.stringify({ resourceNames: resourceNames.slice(i, i + 500) }),
      });
      deleted += unwrap(response, 'Failed to delete contacts').deleted;
    }
    return deleted;
  }

  /**
   * Move contacts to the app's Trash label, or back out of it
   * @param resourceNames - Contacts (sent 1000 per request)
   * @param restore - True to take them out of Trash
   * @returns The Trash label's resource name
   */
  async trashContacts(resourceNames: string[], restore = false): Promise<string> {
    let label = '';
    for (let i = 0; i < resourceNames.length; i += 1000) {
      const response = await this.request<{ trashLabel: string }>(
        `/contacts/trash${restore ? '?restore=true' : ''}`,
        {
          method: 'POST',
          body: JSON.stringify({ resourceNames: resourceNames.slice(i, i + 1000) }),
        },
      );
      label = unwrap(
        response,
        restore ? 'Failed to restore contacts' : 'Failed to move contacts to Trash',
      ).trashLabel;
    }
    return label;
  }

  /**
   * Merge contacts: the server saves the kept contact, then deletes the others.
   * Throws ApiRequestError with code MERGE_WOULD_LOSE_DATA (409) and details
   * { resourceName: fieldNames[] } when deleting would lose data the app does
   * not merge and acknowledgeDataLoss is false.
   * @param request - Merge request
   * @returns The kept contact and the deleted names
   */
  async mergeContacts(request: MergeRequest): Promise<MergeResult> {
    logger.warn(
      {
        context: 'BackendApiClient',
        metadata: {
          primary: request.primaryResourceName,
          deleting: request.deleteResourceNames.length,
        },
      },
      'Merging contacts',
    );
    const response = await this.request<MergeResult>('/contacts/merge', {
      method: 'POST',
      body: JSON.stringify(request),
    });
    return unwrap(response, 'Failed to merge contacts');
  }

  // ===================================
  // Contacts: create and labels (assistant)
  // ===================================

  /**
   * Create a contact
   * @param contact - Field groups of the new contact
   * @returns The created contact
   */
  async createContact(contact: ContactPatch): Promise<Contact> {
    const response = await this.request<Contact>('/contacts', {
      method: 'POST',
      body: JSON.stringify(contact),
    });
    return unwrap(response, 'Failed to create contact');
  }

  /**
   * Add contacts to a label and/or remove them from it
   * @param request - Label (by name, created when missing, or resource name) and members
   * @returns The label's resource name (null when nothing changed)
   */
  async modifyLabel(request: {
    labelName?: string;
    labelResourceName?: string;
    add?: string[];
    remove?: string[];
  }): Promise<string | null> {
    const response = await this.request<{ label: string | null }>('/contacts/labels', {
      method: 'POST',
      body: JSON.stringify(request),
    });
    return unwrap(response, 'Failed to update the label').label;
  }

  // ===================================
  // Atlas (activities, places, visits, health)
  // ===================================

  /** Whether Atlas is configured and its database ready */
  async atlasStatus(): Promise<{ configured: boolean; ready: boolean }> {
    return unwrap(
      await this.request<{ configured: boolean; ready: boolean }>('/atlas/status'),
      'Atlas status failed',
    );
  }

  /** Adds or updates one batch of imported items */
  async atlasIngest(batch: AtlasIngest): Promise<{
    importId: string;
    activities: number;
    places: number;
    visits: number;
    healthSamples: number;
  }> {
    return unwrap(
      await this.request<{
        importId: string;
        activities: number;
        places: number;
        visits: number;
        healthSamples: number;
      }>('/atlas/ingest', { method: 'POST', body: JSON.stringify(batch) }),
      'Import failed',
    );
  }

  /** Activities and visits by time, area, type and text */
  async atlasSearch(query: AtlasQuery): Promise<SearchResult> {
    return unwrap(
      await this.request<SearchResult>('/atlas/search', {
        method: 'POST',
        body: JSON.stringify(query),
      }),
      'Search failed',
    );
  }

  /** Counts per time bucket (timeline) */
  async atlasHistogram(
    from: string,
    to: string,
    bucket: string,
    timeZone?: string,
  ): Promise<HistogramRow[]> {
    const tz = timeZone ? `&tz=${encodeURIComponent(timeZone)}` : '';
    return unwrap(
      await this.request<HistogramRow[]>(
        `/atlas/histogram?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&bucket=${bucket}${tz}`,
      ),
      'Timeline failed',
    );
  }

  /** Totals and imports */
  async atlasSummary(): Promise<AtlasSummary> {
    return unwrap(await this.request<AtlasSummary>('/atlas/summary'), 'Summary failed');
  }

  /** An activity with its track and heart rate */
  async atlasActivity(id: string, maxPoints = 2000): Promise<ActivityDetail> {
    return unwrap(
      await this.request<ActivityDetail>(`/atlas/activities/${id}?maxPoints=${maxPoints}`),
      'Activity failed',
    );
  }

  /** Changes an activity */
  async atlasUpdateActivity(
    id: string,
    patch: { name?: string; type?: string; notes?: string },
  ): Promise<ActivitySummary> {
    return unwrap(
      await this.request<ActivitySummary>(`/atlas/activities/${id}`, {
        method: 'PATCH',
        body: JSON.stringify(patch),
      }),
      'Saving the activity failed',
    );
  }

  /** Deletes an activity */
  async atlasDeleteActivity(id: string): Promise<void> {
    unwrap(
      await this.request<unknown>(`/atlas/activities/${id}`, { method: 'DELETE' }),
      'Deleting the activity failed',
    );
  }

  /** Health values in a range */
  async atlasHealth(
    type: string,
    from: string,
    to: string,
    maxPoints = 1500,
  ): Promise<SeriesPoint[]> {
    return unwrap(
      await this.request<SeriesPoint[]>(
        `/atlas/health?type=${encodeURIComponent(type)}&from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&maxPoints=${maxPoints}`,
      ),
      'Health data failed',
    );
  }

  /** Visits and activities near a point */
  async atlasNear(
    lat: number,
    lon: number,
    radiusM: number,
    from?: string,
    to?: string,
  ): Promise<NearResult> {
    const range = `${from ? `&from=${encodeURIComponent(from)}` : ''}${to ? `&to=${encodeURIComponent(to)}` : ''}`;
    return unwrap(
      await this.request<NearResult>(
        `/atlas/near?lat=${lat}&lon=${lon}&radiusM=${radiusM}${range}`,
      ),
      'Search failed',
    );
  }

  /** Deletes an import and what it brought in */
  async atlasDeleteImport(id: string): Promise<number> {
    return unwrap(
      await this.request<{ removed: number }>(`/atlas/imports/${id}`, { method: 'DELETE' }),
      'Deleting the import failed',
    ).removed;
  }

  /** MCP access tokens */
  async atlasTokens(): Promise<AccessTokenInfo[]> {
    return unwrap(await this.request<AccessTokenInfo[]>('/atlas/tokens'), 'Loading tokens failed');
  }

  /** Creates an MCP access token (the token is returned only now) */
  async atlasCreateToken(name: string): Promise<{ token: string; info: AccessTokenInfo }> {
    return unwrap(
      await this.request<{ token: string; info: AccessTokenInfo }>('/atlas/tokens', {
        method: 'POST',
        body: JSON.stringify({ name }),
      }),
      'Creating the token failed',
    );
  }

  /** Revokes an MCP access token */
  async atlasRevokeToken(id: string): Promise<void> {
    unwrap(
      await this.request<unknown>(`/atlas/tokens/${id}`, { method: 'DELETE' }),
      'Revoking the token failed',
    );
  }

  // ===================================
  // Activity (Gmail + Calendar)
  // ===================================

  /** Starts the opt-in to reading email content, for signatures (leaves the page) */
  connectSignatures(): void {
    window.location.href = `${this.baseUrl}/auth/login?signatures=true`;
  }

  /**
   * Contact details from the signatures in each contact's latest emails (up to 10)
   * @param contacts - Contacts with their names and addresses
   * @returns Per contact id: whether mail was found, and the details
   */
  async enrichSignatures(
    contacts: { id: string; name: string; emails: string[] }[],
  ): Promise<{ results: Record<string, SignatureResult>; rateLimited?: boolean }> {
    return unwrap(
      await this.request<{ results: Record<string, SignatureResult>; rateLimited?: boolean }>(
        '/enrich/signatures',
        { method: 'POST', body: JSON.stringify({ contacts }) },
      ),
      'Failed to read signatures',
    );
  }

  /** Starts the opt-in to Gmail and Calendar access (leaves the page) */
  connectActivity(): void {
    window.location.href = `${this.baseUrl}/auth/login?activity=true`;
  }

  /**
   * Activity snapshot state (cheap; polled during a scan)
   * @returns Status
   */
  async getActivityStatus(): Promise<ActivityStatusResponse> {
    return unwrap(
      await this.request<ActivityStatusResponse>('/activity/status'),
      'Failed to read activity status',
    );
  }

  /**
   * Activity snapshot with the per-address data
   * @returns Activity
   */
  async getActivity(): Promise<ActivityResponse> {
    return unwrap(await this.request<ActivityResponse>('/activity'), 'Failed to read activity');
  }

  /**
   * People seen in mail or meetings whose name or address matches
   * @param queries - Name variants (e.g. Hebrew and English spellings)
   * @param limit - Most matches
   * @returns Whether the directory exists, and the matches
   */
  async findInDirectory(
    queries: string[],
    limit = 10,
  ): Promise<{ available: boolean; matches: DirectoryMatch[] }> {
    const q = encodeURIComponent(queries.map((x) => x.replace(/\|/g, ' ')).join('|'));
    return unwrap(
      await this.request<{ available: boolean; matches: DirectoryMatch[] }>(
        `/activity/directory?q=${q}&limit=${limit}`,
      ),
      'Failed to search mail and calendar',
    );
  }

  /**
   * Addresses from mail and meetings that match contacts without an email, by name
   * @param contacts - Contact ids and their name variants
   * @returns Candidates per contact id, best first
   */
  async suggestEmails(
    contacts: { id: string; names: string[] }[],
  ): Promise<{ available: boolean; suggestions: Record<string, DirectoryMatch[]> }> {
    return unwrap(
      await this.request<{ available: boolean; suggestions: Record<string, DirectoryMatch[]> }>(
        '/activity/suggest-emails',
        { method: 'POST', body: JSON.stringify({ contacts }) },
      ),
      'Failed to look for email addresses',
    );
  }

  /**
   * Start bringing the snapshot up to date
   * @param full - Rescan the whole window
   * @returns Status after queueing
   */
  async refreshActivity(full = false): Promise<ActivityStatusResponse> {
    return unwrap(
      await this.request<ActivityStatusResponse>(`/activity/refresh${full ? '?full=true' : ''}`, {
        method: 'POST',
      }),
      'Failed to start the activity scan',
    );
  }

  /**
   * Forget the stored activity
   * @returns Empty status
   */
  async forgetActivity(): Promise<ActivityStatusResponse> {
    return unwrap(
      await this.request<ActivityStatusResponse>('/activity', { method: 'DELETE' }),
      'Failed to forget activity',
    );
  }

  // ===================================
  // Assistant (Gemini proxy)
  // ===================================

  /**
   * Whether the assistant is configured
   * @returns Enabled flag and model
   */
  async getAssistantStatus(): Promise<{ enabled: boolean; model: string }> {
    return unwrap(
      await this.request<{ enabled: boolean; model: string }>('/assistant/status'),
      'Failed to read assistant status',
    );
  }

  /**
   * One Gemini generateContent call through the backend
   * @param request - systemInstruction, contents, tools, toolConfig
   * @returns Gemini's response
   */
  async assistantGenerate(request: GeminiRequest): Promise<GeminiResponse> {
    return unwrap(
      await this.request<GeminiResponse>('/assistant/generate', {
        method: 'POST',
        body: JSON.stringify(request),
      }),
      'The assistant failed to answer',
    );
  }

  /**
   * Web search with Google Search grounding
   * @param query - What to look up
   * @returns Answer and sources
   */
  async assistantSearch(
    query: string,
  ): Promise<{ text: string; sources: { title?: string; uri?: string }[] }> {
    return unwrap(
      await this.request<{ text: string; sources: { title?: string; uri?: string }[] }>(
        '/assistant/search',
        { method: 'POST', body: JSON.stringify({ query }) },
      ),
      'Web search failed',
    );
  }

  /**
   * Update one field group of a contact (inline editing)
   * Story 3.1 - Service Layer for Google Contacts API Operations
   * @param resourceName - Contact resource name (e.g., "people/c123")
   * @param fieldPath - Field group to replace (e.g., "names", "phoneNumbers")
   * @param newValue - New value for the whole group
   * @param etag - Etag the contact was last read with
   * @returns Promise resolving to updated Contact
   */
  async updateContactField(
    resourceName: string,
    fieldPath: string,
    newValue: unknown,
    etag?: string | null,
  ): Promise<Contact> {
    logger.info(
      {
        context: 'BackendApiClient/updateContactField',
        metadata: { resourceName, fieldPath },
      },
      'Updating contact field',
    );

    return this.updateContact(resourceName, { etag, [fieldPath]: newValue } as ContactPatch);
  }
}

// Export singleton instance
export const backendApiClient = new BackendApiClient();
