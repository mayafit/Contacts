/**
 * @fileoverview Contacts workspace: app shell with the Contacts, Insights and
 * Duplicates views, global search and the contact card.
 * @module Contacts/components/ContactsHome
 *
 * Story 3.7: ConflictResolutionDialog wiring for 412 conflict handling.
 * Story 4.2: app shell and views.
 */

import React, { useCallback, useDeferredValue, useEffect, useMemo, useRef, useState } from 'react';
import { useSelector } from 'react-redux';
import { useNavigate } from 'react-router';
import {
  Alert,
  Avatar,
  Box,
  Button,
  Chip,
  CircularProgress,
  IconButton,
  InputBase,
  ListItemIcon,
  Menu,
  MenuItem,
  Paper,
  Snackbar,
  Select,
  Skeleton,
  Tab,
  Tabs,
  Tooltip,
  Typography,
  useMediaQuery,
  useTheme,
} from '@mui/material';
import {
  AutoAwesome,
  CallMerge,
  PhonelinkRing,
  SaveAlt,
  Close,
  ContactPhone,
  DeleteOutline,
  Logout as LogoutIcon,
  Refresh,
  Search,
  Star,
  StarBorder,
  ViewColumn,
} from '@mui/icons-material';
import { logoutUser } from '../redux/slices/auth/authSlice';
import TrashView from './cleanup/TrashView';
import { isFavourite, STAR_COLOR } from './common/FavouriteStar';
import { onNotify } from './common/notify';
import { useLabelSync } from '../hooks/useLabelSync';
import { changeMembership, STARRED } from '../redux/thunks/labelThunks';
import { useSegments } from '../hooks/useSegments';
import { useProfiles } from '../hooks/useProfiles';
import type { DossierData } from './card/Dossier';
import ProfilesCard from './insights/ProfilesCard';
import {
  labelDiff,
  mainCountryOf,
  matchesSegment,
  membersOf,
  newSegmentId,
  type Segment,
  type SegmentContext,
} from '../model/segments';
import SegmentMenu, { type SegmentFigures } from './segments/SegmentMenu';
import SegmentEditor from './segments/SegmentEditor';
import LabelsManager from './segments/LabelsManager';
import UpdateLabelDialog from './segments/UpdateLabelDialog';
import LabelMenu from './segments/LabelMenu';
import FavouritesCard from './insights/FavouritesCard';
import SuggestedSegments from './insights/SuggestedSegments';
import { trashContacts } from '../redux/thunks/cleanupThunks';
import { selectIsAuthenticated, selectUser } from '../redux/slices/auth/selectors';
import SignedOutHome from './SignedOutHome';
import { selectConflictedOperations } from '../redux/slices/syncQueue/selectors';
import { operationSuccess, operationFailed } from '../redux/slices/syncQueue/syncQueueSlice';
import {
  contactAdded,
  contactUpdated,
  fetchContactGroups,
  fetchContacts,
} from '../redux/slices/contacts/contactsSlice';
import {
  selectAllContacts,
  selectContactEntities,
  selectContactGroups,
  selectContactsError,
  selectContactsLoading,
} from '../redux/slices/contacts/selectors';
import { executeFieldUpdate } from '../redux/thunks/syncThunks';
import { GoogleContactsService } from '../services/GoogleContactsService';
import { logger } from '../../../shared/logger';
import ContactsTable from './table/ContactsTable';
import ColumnConfigDialog from '../features/columnConfig/components/ColumnConfigDialog';
import { ConflictResolutionDialog } from './ConflictResolutionDialog';
import ContactCardDrawer from './card/ContactCardDrawer';
import InsightsView from './insights/InsightsView';
import DuplicatesView from './duplicates/DuplicatesView';
import DeleteDialog from './cleanup/DeleteDialog';
import MergeDialog from './cleanup/MergeDialog';
import OrgSuggestionsDialog from './insights/OrgSuggestionsDialog';
import { analyzeOrganizations, type DomainInsight } from '../model/organizations';
import { useAppSelector, useAppDispatch } from '../types/hooks';
import type { Contact, ContactGroup } from '../types/Contact';
import {
  clean,
  getDisplayName,
  getPhoneticName,
  hasName,
  matchesQuery,
  searchTextOf,
} from '../model/contactView';
import { duplicateMemberSet, findDuplicateGroups } from '../model/duplicates';
import { computeContactStats, type ContactFilter } from '../model/stats';
import { tokens } from '../theme/contactsTheme';
import { useActivity } from '../hooks/useActivity';
import { buildActivityIndex } from '../model/activity';
import ActivitySection from './insights/ActivitySection';
import ReachOutSection from './insights/ReachOutSection';
import SuggestEmailsDialog from './insights/SuggestEmailsDialog';
import SignatureDialog from './insights/SignatureDialog';
import AssistantPanel from './assistant/AssistantPanel';
import MobileContactList from './table/MobileContactList';
import DeviceSyncDialog from './device/DeviceSyncDialog';
import { platformOf, saveToDevice } from '../device/device';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import { selectColumnConfig, setColumnConfig } from '../redux/slices/ui/uiSlice';
import { fs } from '../settings/uiSettings';

type View = 'contacts' | 'insights' | 'duplicates' | 'trash';

/** Label that holds the app's Trash (same name as the backend's) */
export const TRASH_LABEL_NAME = 'Contacts app – Trash';

const FILTERS: { id: ContactFilter; label: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'favourites', label: 'Favourites' },
  { id: 'missingPhonetic', label: 'Missing phonetic' },
  { id: 'noPhone', label: 'No phone' },
  { id: 'noEmail', label: 'No email' },
  { id: 'noName', label: 'No name' },
  { id: 'duplicates', label: 'Duplicates' },
];

const fmt = new Intl.NumberFormat('en-US');

/**
 * Contacts workspace page.
 * @returns Page element
 */
const SignedInHome: React.FC = () => {
  const dispatch = useAppDispatch();
  const navigate = useNavigate();
  const user = useSelector(selectUser);
  const allContacts = useSelector(selectAllContacts) as Contact[];
  const loading = useSelector(selectContactsLoading);
  const error = useSelector(selectContactsError);
  const groups = useAppSelector(selectContactGroups) as Record<string, ContactGroup>;
  // Contacts in the app's Trash are hidden from every view except Trash
  const trashLabel = useMemo(
    () =>
      Object.values(groups).find(
        (g) => g.name === TRASH_LABEL_NAME && g.groupType === 'USER_CONTACT_GROUP',
      )?.resourceName,
    [groups],
  );
  const contacts = useMemo(
    () => (trashLabel ? allContacts.filter((c) => !c.labels?.includes(trashLabel)) : allContacts),
    [allContacts, trashLabel],
  );
  const trashed = useMemo(
    () => (trashLabel ? allContacts.filter((c) => c.labels?.includes(trashLabel)) : []),
    [allContacts, trashLabel],
  );

  const [view, setView] = useState<View>('contacts');
  const [query, setQuery] = useState('');
  const deferredQuery = useDeferredValue(query);
  const [filter, setFilter] = useState<ContactFilter>('all');
  const [label, setLabel] = useState<string>('');
  const [cardFor, setCardFor] = useState<string | null>(null);
  // Story 5.1/5.2: checked rows and the delete / merge dialogs
  const [checked, setChecked] = useState<Set<string>>(new Set());
  const [deleteTargets, setDeleteTargets] = useState<Contact[] | null>(null);
  const [mergeTargets, setMergeTargets] = useState<Contact[] | null>(null);
  const [deletePermanent, setDeletePermanent] = useState(false);
  const [toast, setToast] = useState<string | null>(null);
  // Messages from stars and label chips anywhere in the workspace
  useEffect(() => onNotify(setToast), []);
  // Favourites and labels in step with Google (AD-44 rule 3)
  useLabelSync(allContacts);
  const [reviewDomain, setReviewDomain] = useState<DomainInsight | null>(null);
  const [isColumnConfigOpen, setIsColumnConfigOpen] = useState(false);
  const [accountAnchor, setAccountAnchor] = useState<HTMLElement | null>(null);
  // A set of contacts shown in the table (from Insights or the assistant)
  const [selection, setSelection] = useState<{ title: string; ids: Set<string> } | null>(null);
  const [assistantOpen, setAssistantOpen] = useState(false);
  const [phoneSyncOpen, setPhoneSyncOpen] = useState(false);
  const [suggestOpen, setSuggestOpen] = useState(false);
  // Signature reader: the most-emailed contacts ('auto'), or chosen ones
  const [signatureTargets, setSignatureTargets] = useState<Contact[] | 'auto' | null>(null);
  // A message for the assistant to answer as soon as it opens (e.g. "Draft")
  const [assistantRequest, setAssistantRequest] = useState<{ text: string; id: number } | null>(
    null,
  );
  // Phone screens get a list with call buttons instead of the table
  const theme = useTheme();
  const isPhone = useMediaQuery(theme.breakpoints.down('sm'));
  const onMobileDevice = platformOf() !== 'desktop';
  const [assistantInfo, setAssistantInfo] = useState<{ enabled: boolean; model: string } | null>(
    null,
  );
  const entities = useAppSelector(selectContactEntities) as Record<string, Contact | undefined>;
  const columnConfig = useAppSelector(selectColumnConfig);

  // Epic 7: email / meeting activity
  const activity = useActivity();
  const activityIndex = useMemo(
    () => (activity.people ? buildActivityIndex(contacts, activity.people) : undefined),
    [contacts, activity.people],
  );

  // Epic 17: segments (AD-45)
  const segmentsState = useSegments();
  const { segments } = segmentsState;
  const [segmentId, setSegmentId] = useState<string | null>(null);
  const [editingSegment, setEditingSegment] = useState<Segment | null>(null);
  const [labelsOpen, setLabelsOpen] = useState(false);
  const [updateFor, setUpdateFor] = useState<Segment | null>(null);

  useEffect(() => {
    backendApiClient
      .getAssistantStatus()
      .then(setAssistantInfo)
      .catch(() => setAssistantInfo(null));
  }, []);

  // Back from connecting Gmail & Calendar: show where the result appears
  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    if (params.get('connected') === 'signatures') {
      setView('insights');
      if (params.get('declined')) {
        setToast('Email content was not allowed');
      } else {
        setSignatureTargets('auto');
      }
      window.history.replaceState(null, '', window.location.pathname);
    }
    if (params.get('connected') === 'activity') {
      setView('insights');
      if (params.get('declined')) {
        setToast('Gmail & Calendar were not connected');
      }
      window.history.replaceState(null, '', window.location.pathname);
    }
  }, []);

  // Once activity exists, offer the "Last in touch" column (once per browser)
  useEffect(() => {
    if (!activityIndex || activityIndex.size === 0) {
      return;
    }
    try {
      if (localStorage.getItem('contacts.lastInTouchOffered')) {
        return;
      }
      localStorage.setItem('contacts.lastInTouchOffered', '1');
    } catch {
      return;
    }
    if (!columnConfig.visibleColumns.includes('lastInTouch')) {
      dispatch(
        setColumnConfig({
          visibleColumns: [...columnConfig.visibleColumns, 'lastInTouch'],
          columnOrder: [
            ...columnConfig.columnOrder.filter((c) => c !== 'lastInTouch'),
            'lastInTouch',
          ],
        }),
      );
    }
  }, [activityIndex, columnConfig, dispatch]);
  const searchRef = useRef<HTMLInputElement>(null);

  // Story 3.7: Conflict resolution state
  const conflictedOperations = useAppSelector(selectConflictedOperations);
  const firstConflict = conflictedOperations.length > 0 ? conflictedOperations[0] : null;

  const load = useCallback(() => {
    dispatch(fetchContacts());
    dispatch(fetchContactGroups());
  }, [dispatch]);

  useEffect(() => {
    load();
  }, [load]);

  // "/" focuses search, like most web apps
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement;
      if (e.key === '/' && !/INPUT|TEXTAREA/.test(target.tagName) && !target.isContentEditable) {
        e.preventDefault();
        searchRef.current?.focus();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const duplicateGroups = useMemo(() => findDuplicateGroups(contacts), [contacts]);
  const duplicateMembers = useMemo(() => duplicateMemberSet(duplicateGroups), [duplicateGroups]);
  const stats = useMemo(
    () => computeContactStats(contacts, groups, duplicateGroups),
    [contacts, groups, duplicateGroups],
  );
  const orgInsights = useMemo(() => analyzeOrganizations(contacts), [contacts]);
  const searchIndex = useMemo(
    () => new Map(contacts.map((c) => [c.resourceName, searchTextOf(c)])),
    [contacts],
  );
  const mainCountry = useMemo(() => mainCountryOf(contacts), [contacts]);
  const segmentCtx = useMemo<SegmentContext>(
    () => ({ activity: activityIndex ?? null, mainCountry, now: new Date(), searchIndex }),
    [activityIndex, mainCountry, searchIndex],
  );
  const segmentMembers = useMemo(
    () => new Map(segments.map((s) => [s.id, membersOf(contacts, s, segmentCtx)])),
    [segments, contacts, segmentCtx],
  );
  const segmentFigures = useMemo(() => {
    const figures = new Map<string, SegmentFigures>();
    segments.forEach((s) => {
      const members = segmentMembers.get(s.id) ?? [];
      // A label deleted since is no link
      const diff = s.label && groups[s.label] ? labelDiff(members, contacts, s.label) : null;
      figures.set(s.id, {
        count: members.length,
        diff: diff ? { add: diff.add.length, remove: diff.remove.length } : undefined,
      });
    });
    return figures;
  }, [segments, segmentMembers, contacts, groups]);
  const activeSegment = segments.find((s) => s.id === segmentId) ?? null;
  // Epic 18: public profiles and the dossier (AD-46)
  const profiles = useProfiles(contacts);
  const dossier = useMemo<DossierData>(
    () => ({
      contacts,
      activity: activityIndex ?? null,
      threads: activity.threads,
      segments,
      segmentCtx,
      gravatar: profiles.byContact,
      signaturesAllowed: !!activity.status?.connected.signatures,
      onOpenCard: setCardFor,
    }),
    [
      contacts,
      activityIndex,
      activity.threads,
      activity.status,
      segments,
      segmentCtx,
      profiles.byContact,
    ],
  );

  const predicates = useMemo<Record<ContactFilter, (c: Contact) => boolean>>(
    () => ({
      all: () => true,
      favourites: isFavourite,
      missingPhonetic: (c) => !getPhoneticName(c),
      noPhone: (c) => !c.phoneNumbers?.some((p) => p.value),
      noEmail: (c) => !c.emailAddresses?.some((e) => e.value),
      noName: (c) => !hasName(c),
      duplicates: (c) => duplicateMembers.has(c.resourceName),
    }),
    [duplicateMembers],
  );

  const filterCounts = useMemo(() => {
    const counts = {} as Record<ContactFilter, number>;
    FILTERS.forEach((f) => {
      counts[f.id] = contacts.filter(predicates[f.id]).length;
    });
    return counts;
  }, [contacts, predicates]);

  const labelOptions = useMemo(
    () =>
      Object.values(groups)
        .filter(
          (g) => g.groupType === 'USER_CONTACT_GROUP' || g.resourceName === 'contactGroups/starred',
        )
        .filter((g) => (g.memberCount ?? 0) > 0)
        .sort((a, b) => a.name.localeCompare(b.name)),
    [groups],
  );

  const visible = useMemo(
    () =>
      contacts.filter(
        (c) =>
          predicates[filter](c) &&
          (!selection || selection.ids.has(c.resourceName)) &&
          (!label || (c.labels ?? []).includes(label)) &&
          (!activeSegment || matchesSegment(c, activeSegment, segmentCtx)) &&
          matchesQuery(c, deferredQuery, searchIndex.get(c.resourceName)),
      ),
    [
      contacts,
      predicates,
      filter,
      selection,
      label,
      activeSegment,
      segmentCtx,
      deferredQuery,
      searchIndex,
    ],
  );

  // A search that finds nobody says where its matches are: hidden by the filters, or in the
  // Trash (left out of every view)
  const elsewhere = useMemo(() => {
    if (visible.length > 0 || !clean(deferredQuery)) {
      return { filtered: 0, trash: [] as string[] };
    }
    return {
      filtered: contacts.filter((c) =>
        matchesQuery(c, deferredQuery, searchIndex.get(c.resourceName)),
      ).length,
      trash: trashed.filter((c) => matchesQuery(c, deferredQuery)).map((c) => c.resourceName),
    };
  }, [visible.length, deferredQuery, contacts, searchIndex, trashed]);
  // The Trash opened from there ticks those matches
  const [trashPreselect, setTrashPreselect] = useState<string[] | undefined>(undefined);

  /** Shows a set of contacts in the table, replacing other filters */
  const showContacts = useCallback((ids: string[], title: string) => {
    setSelection({ title, ids: new Set(ids) });
    setFilter('all');
    setLabel('');
    setQuery('');
    setView('contacts');
  }, []);

  /** Creates a contact for an address you write to (Insights) and opens it */
  const addContactForEmail = async (email: string) => {
    try {
      const created = await backendApiClient.createContact({
        etag: '',
        emailAddresses: [{ value: email, type: 'other' }],
      });
      dispatch(contactAdded(created));
      setCardFor(created.resourceName);
      setToast(`Added ${email}. Fill in the name in the card.`);
    } catch (e) {
      setToast((e as Error).message || 'Could not add the contact');
    }
  };

  const showFilter = (f: ContactFilter) => {
    setSelection(null);
    setFilter(f);
    setLabel('');
    setQuery('');
    setView('contacts');
  };

  const assistantContext = useMemo(
    () => ({
      contacts,
      trashed,
      groups,
      stats,
      duplicateGroups,
      orgInsights,
      activity: activityIndex,
      activityReady: !!activityIndex,
      threads: activity.threads,
      signaturesReady: !!activity.status?.connected.signatures,
      user: { name: user?.name, email: user?.email },
      openContact: setCardFor,
      showContacts,
      openMerge: setMergeTargets,
    }),
    [
      contacts,
      trashed,
      groups,
      stats,
      duplicateGroups,
      orgInsights,
      activityIndex,
      activity.threads,
      activity.status,
      user,
      showContacts,
    ],
  );

  /**
   * The search box: in the top bar on wider screens, and as its own full-width
   * row under it on phones (a 110px box in the bar was too small to use).
   * @param phone - The phone row version
   * @returns Search box
   */
  const renderSearch = (phone: boolean) => (
    <Box
      sx={{
        display: phone ? { xs: 'flex', sm: 'none' } : { xs: 'none', sm: 'flex' },
        alignItems: 'center',
        gap: 1,
        px: 1.5,
        height: phone ? 44 : 38,
        width: phone ? '100%' : { sm: 260, md: 380 },
        flexShrink: 1,
        minWidth: 0,
        borderRadius: '10px',
        bgcolor: tokens.page,
        border: `1px solid ${tokens.border}`,
        '&:focus-within': { borderColor: tokens.accent },
      }}
    >
      <Search sx={{ fontSize: fs(20), color: tokens.textMuted }} />
      <InputBase
        inputRef={phone ? undefined : searchRef}
        value={query}
        onChange={(e) => {
          setQuery(e.target.value);
          setView('contacts');
        }}
        onKeyDown={(e) => e.key === 'Escape' && setQuery('')}
        placeholder={phone ? 'Search contacts' : 'Search name, phonetic, phone, email…'}
        inputProps={{ 'aria-label': 'Search contacts', dir: 'auto', enterKeyHint: 'search' }}
        // 16px on phones: iOS zooms the page into any smaller input
        sx={{ flex: 1, fontSize: phone ? 16 : 14 }}
      />
      {query ? (
        <IconButton
          size={phone ? 'medium' : 'small'}
          onClick={() => setQuery('')}
          aria-label="Clear search"
        >
          <Close sx={{ fontSize: phone ? 20 : 16 }} />
        </IconButton>
      ) : (
        <Box
          component="kbd"
          sx={{
            fontSize: fs(12),
            color: tokens.textMuted,
            border: `1px solid ${tokens.borderStrong}`,
            borderRadius: '5px',
            px: 0.75,
            display: { xs: 'none', md: 'block' },
          }}
        >
          /
        </Box>
      )}
    </Box>
  );

  const handleSignOut = () => {
    logger.info({ context: 'ContactsHome/handleSignOut' }, 'User initiated sign out');
    setAccountAnchor(null);
    // Ends the server session as well; the page alone forgetting the user
    // left the session alive, so a reload signed straight back in.
    void dispatch(logoutUser()).finally(() => navigate('/contacts', { replace: true }));
  };

  /**
   * Story 3.7 AC4: Keep user's change — fetch fresh etag, retry the update
   */
  const handleKeepMine = useCallback(async () => {
    if (!firstConflict) {
      return;
    }
    const { id, resourceName, fieldPath, newValue, oldValue } = firstConflict;
    logger.info(
      {
        context: 'ContactsHome/handleKeepMine',
        metadata: { operationId: id, resourceName, fieldPath },
      },
      'Conflict resolved: user kept their change',
    );
    // Take Google's current etag first, then re-apply the change on top of it
    const remote = await GoogleContactsService.getContact(resourceName);
    if (remote.success && remote.data) {
      dispatch(contactUpdated(remote.data));
    }
    dispatch(operationSuccess(id));
    dispatch(executeFieldUpdate({ resourceName, fieldPath, newValue, oldValue }));
  }, [firstConflict, dispatch]);

  /**
   * Story 3.7 AC5: Use remote value — rollback optimistic update
   */
  const handleUseRemote = useCallback(async () => {
    if (!firstConflict) {
      return;
    }
    const { id, resourceName, fieldPath } = firstConflict;
    const remoteResult = await GoogleContactsService.getContact(resourceName);
    if (remoteResult.success && remoteResult.data) {
      dispatch(contactUpdated(remoteResult.data));
      dispatch(operationSuccess(id));
      logger.info(
        {
          context: 'ContactsHome/handleUseRemote',
          metadata: { operationId: id, resourceName, fieldPath },
        },
        'Conflict resolved: remote value applied',
      );
    } else {
      // Cannot safely apply partial data — would corrupt the entity store
      logger.error(
        { context: 'ContactsHome/handleUseRemote', metadata: { operationId: id, resourceName } },
        'Failed to fetch remote contact for rollback — operation remains in conflict state',
      );
      dispatch(operationFailed({ id, error: 'Failed to fetch remote contact. Please try again.' }));
    }
  }, [firstConflict, dispatch]);

  const userInitial = (user?.name && user.name !== 'User' ? user.name : 'Me')
    .slice(0, 1)
    .toUpperCase();

  const byResourceName = useMemo(
    () => new Map(contacts.map((c) => [c.resourceName, c])),
    [contacts],
  );
  const checkedContacts = useMemo(
    () =>
      Array.from(checked)
        .map((rn) => byResourceName.get(rn))
        .filter(Boolean) as Contact[],
    [checked, byResourceName],
  );

  /** Adds the contacts to favourites or removes them (AD-44 rule 2) */
  const setFavourites = useCallback(
    async (list: Contact[], on: boolean) => {
      const targets = list.filter((c) => isFavourite(c) !== on).map((c) => c.resourceName);
      const refused = await changeMembership(dispatch, targets, STARRED, on);
      setToast(
        refused
          ? `Favourites not changed: ${refused}`
          : `${fmt.format(targets.length)} ${on ? 'added to' : 'removed from'} favourites`,
      );
    },
    [dispatch],
  );

  // Checks apply to what is on screen: a new search or filter starts a new
  // selection, so a delete can never include rows the user cannot see.
  useEffect(() => {
    setChecked(new Set());
  }, [filter, label, deferredQuery]);

  /** Forget removed contacts everywhere they may still be referenced */
  const forget = (removed: string[]) => {
    setChecked((prev) => {
      const next = new Set(prev);
      removed.forEach((rn) => next.delete(rn));
      return next;
    });
    if (cardFor && removed.includes(cardFor)) {
      setCardFor(null);
    }
  };

  const handleDeleted = (count: number, permanent: boolean) => {
    const removed = (deleteTargets ?? []).map((c) => c.resourceName);
    setDeleteTargets(null);
    forget(removed);
    const what = count === 1 ? 'Contact' : `${count} contacts`;
    setToast(permanent ? `${what} deleted permanently` : `${what} moved to Trash`);
  };

  const restore = async (list: Contact[]) => {
    try {
      await dispatch(
        trashContacts({ resourceNames: list.map((c) => c.resourceName), restore: true }),
      ).unwrap();
      setToast(list.length === 1 ? 'Contact restored' : `${list.length} contacts restored`);
    } catch (e) {
      setToast((e as { message?: string })?.message ?? 'Restore failed');
    }
  };

  const handleMerged = (kept: Contact, removed: number, notCopied: Record<string, string[]>) => {
    // What could not be carried over stays on the merged-away contact
    const leftBehind = Object.entries(notCopied).map(([rn, what]) => {
      const c = (mergeTargets ?? []).find((t) => t.resourceName === rn);
      return `${what.join(', ')} of ${c ? getDisplayName(c) : 'a merged contact'}`;
    });
    const removedNames = (mergeTargets ?? [])
      .map((c) => c.resourceName)
      .filter((rn) => rn !== kept.resourceName);
    setMergeTargets(null);
    forget(removedNames);
    setChecked(new Set());
    setToast(
      `Merged ${removed + 1} contacts into one` +
        (leftBehind.length
          ? ` · not carried over (still on the merged-away contact): ${leftBehind.join('; ')}`
          : ''),
    );
  };

  const renderContactsView = () => (
    <Box
      sx={{
        display: 'flex',
        flexDirection: 'column',
        flex: 1,
        minHeight: 0,
        p: { xs: 1.5, md: 2.5 },
        gap: 1.5,
      }}
    >
      {checkedContacts.length > 0 && (
        <Box
          role="toolbar"
          aria-label="Selected contacts"
          sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 1,
            px: 1.5,
            py: 0.75,
            borderRadius: '10px',
            bgcolor: 'rgba(57,135,229,0.12)',
            border: `1px solid rgba(57,135,229,0.35)`,
          }}
        >
          <Typography variant="body2" sx={{ fontWeight: 600, mr: 1 }}>
            {fmt.format(checkedContacts.length)} selected
          </Typography>
          <Button
            size="small"
            startIcon={<CallMerge />}
            disabled={checkedContacts.length < 2 || checkedContacts.length > 20}
            onClick={() => setMergeTargets(checkedContacts)}
          >
            Merge
          </Button>
          <Button
            size="small"
            color="error"
            startIcon={<DeleteOutline />}
            onClick={() => setDeleteTargets(checkedContacts)}
          >
            Delete
          </Button>
          <Button
            size="small"
            startIcon={<Star sx={{ color: STAR_COLOR }} />}
            onClick={() => void setFavourites(checkedContacts, true)}
            disabled={checkedContacts.every(isFavourite)}
          >
            Add to favourites
          </Button>
          <Button
            size="small"
            startIcon={<StarBorder />}
            onClick={() => void setFavourites(checkedContacts, false)}
            disabled={!checkedContacts.some(isFavourite)}
          >
            Remove from favourites
          </Button>
          <LabelMenu contacts={checkedContacts} groups={groups} onDone={setToast} />
          <Button
            size="small"
            startIcon={<SaveAlt />}
            onClick={async () => {
              const how = await saveToDevice(checkedContacts, `${checkedContacts.length} contacts`);
              setToast(
                how === 'shared'
                  ? 'Choose Contacts in the share sheet'
                  : 'Downloaded as a .vcf file',
              );
            }}
          >
            Save to device
          </Button>
          <Box sx={{ flex: 1 }} />
          <Button size="small" color="inherit" onClick={() => setChecked(new Set())}>
            Clear
          </Button>
        </Box>
      )}
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, flexWrap: 'wrap' }}>
        {FILTERS.map((f) => {
          const active = filter === f.id;
          const count = filterCounts[f.id] ?? 0;
          if (f.id !== 'all' && count === 0 && !active) {
            return null;
          }
          return (
            <Chip
              key={f.id}
              label={
                <Box
                  component="span"
                  sx={{ display: 'inline-flex', gap: 0.75, alignItems: 'baseline' }}
                >
                  {f.label}
                  <Box component="span" sx={{ opacity: 0.7, fontVariantNumeric: 'tabular-nums' }}>
                    {fmt.format(count)}
                  </Box>
                </Box>
              }
              onClick={() => setFilter(f.id)}
              color={active ? 'primary' : 'default'}
              variant={active ? 'filled' : 'outlined'}
              sx={{ borderColor: tokens.borderStrong }}
            />
          );
        })}
        {selection && (
          <Chip
            color="primary"
            label={`${selection.title} · ${fmt.format(selection.ids.size)}`}
            onDelete={() => setSelection(null)}
            sx={{ maxWidth: 360 }}
          />
        )}
        {labelOptions.length > 0 && (
          <Select
            size="small"
            value={label}
            displayEmpty
            onChange={(e) => setLabel(e.target.value)}
            aria-label="Filter by label"
            sx={{ minWidth: 150, height: 32, fontSize: fs(14), borderRadius: '8px' }}
            renderValue={(v) => (v ? (groups[v]?.name ?? 'Label') : 'All labels')}
          >
            <MenuItem value="">All labels</MenuItem>
            {labelOptions.map((g) => (
              <MenuItem key={g.resourceName} value={g.resourceName}>
                {g.name}
                <Box component="span" sx={{ ml: 'auto', pl: 2, color: tokens.textMuted }}>
                  {g.memberCount}
                </Box>
              </MenuItem>
            ))}
          </Select>
        )}
        <SegmentMenu
          segments={segments}
          figures={segmentFigures}
          active={activeSegment}
          onSelect={setSegmentId}
          onNew={() =>
            setEditingSegment({
              id: newSegmentId(),
              name: '',
              match: 'all',
              rules: [],
              label: null,
            })
          }
          onEdit={setEditingSegment}
          onManageLabels={() => setLabelsOpen(true)}
          onUpdateLabel={setUpdateFor}
        />
        <Box sx={{ flex: 1 }} />
        <Typography
          variant="body2"
          sx={{ color: tokens.textMuted, fontVariantNumeric: 'tabular-nums' }}
        >
          {visible.length === contacts.length
            ? `${fmt.format(contacts.length)} contacts`
            : `${fmt.format(visible.length)} of ${fmt.format(contacts.length)}`}
        </Typography>
        <Tooltip title="Choose columns">
          <IconButton
            onClick={() => setIsColumnConfigOpen(true)}
            aria-label="Configure columns"
            size="small"
          >
            <ViewColumn fontSize="small" />
          </IconButton>
        </Tooltip>
      </Box>

      <Paper
        variant="outlined"
        sx={{
          flex: 1,
          minHeight: 0,
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
          borderColor: tokens.border,
          bgcolor: tokens.surface,
        }}
      >
        {loading && contacts.length === 0 ? (
          <Box sx={{ p: 2 }} aria-busy="true" aria-label="Loading contacts">
            {Array.from({ length: 10 }).map((_, i) => (
              <Box key={i} sx={{ display: 'flex', gap: 1.5, alignItems: 'center', height: 52 }}>
                <Skeleton variant="circular" width={32} height={32} />
                <Skeleton width="22%" />
                <Skeleton width="16%" />
                <Skeleton width="14%" />
                <Skeleton width="20%" />
              </Box>
            ))}
          </Box>
        ) : error ? (
          <Box sx={{ p: 3 }}>
            <Alert
              severity="error"
              action={
                <Button color="inherit" size="small" onClick={load}>
                  Retry
                </Button>
              }
            >
              {error.message}
            </Alert>
          </Box>
        ) : visible.length === 0 ? (
          <Box sx={{ p: 6, textAlign: 'center' }}>
            <Typography variant="body1" sx={{ color: tokens.textSecondary }}>
              {contacts.length === 0 ? 'No contacts in this Google account.' : 'No contacts match.'}
            </Typography>
            {contacts.length > 0 && (
              <Button sx={{ mt: 1 }} onClick={() => showFilter('all')}>
                Clear search and filters
              </Button>
            )}
            {elsewhere.filtered > 0 && (
              <Typography variant="body2" sx={{ color: tokens.textMuted, mt: 1 }}>
                {elsewhere.filtered === 1
                  ? '1 contact matches'
                  : `${elsewhere.filtered} contacts match`}{' '}
                outside the current filters.
              </Typography>
            )}
            {elsewhere.trash.length > 0 && (
              <Button
                sx={{ mt: 1, display: 'block', mx: 'auto' }}
                onClick={() => {
                  setTrashPreselect(elsewhere.trash);
                  setView('trash');
                }}
              >
                {elsewhere.trash.length === 1 ? '1 match' : `${elsewhere.trash.length} matches`} in
                Trash
              </Button>
            )}
          </Box>
        ) : isPhone ? (
          <MobileContactList contacts={visible} onOpenCard={setCardFor} activity={activityIndex} />
        ) : (
          <ContactsTable
            contacts={visible}
            groups={groups}
            selectedResourceName={cardFor}
            onOpenCard={setCardFor}
            scrollResetKey={`${filter}|${label}|${deferredQuery}|${selection?.title ?? ''}`}
            checked={checked}
            onCheckedChange={setChecked}
            activity={activityIndex}
          />
        )}
      </Paper>
      <Typography
        variant="caption"
        sx={{ color: tokens.textMuted, textAlign: 'center', display: { xs: 'none', sm: 'block' } }}
      >
        Click a cell to edit · Double-click a row for the full card · Enter saves and moves down ·
        Tab moves right · Esc cancels
      </Typography>
    </Box>
  );

  return (
    <Box
      sx={{
        flex: 1,
        minHeight: 0,
        height: '100%',
        display: 'flex',
        flexDirection: 'column',
        bgcolor: tokens.page,
        color: tokens.text,
      }}
    >
      {/* Top bar */}
      <Box
        component="header"
        sx={{
          display: 'flex',
          alignItems: 'center',
          gap: { xs: 1, md: 2 },
          px: { xs: 1.5, md: 2.5 },
          height: 60,
          flexShrink: 0,
          borderBottom: `1px solid ${tokens.border}`,
          bgcolor: tokens.surface,
        }}
      >
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.25, mr: { md: 1 } }}>
          <Box
            sx={{
              width: 32,
              height: 32,
              borderRadius: '9px',
              bgcolor: tokens.accent,
              display: 'grid',
              placeItems: 'center',
            }}
          >
            <ContactPhone sx={{ fontSize: fs(18), color: '#fff' }} />
          </Box>
          <Typography variant="h3" sx={{ display: { xs: 'none', sm: 'block' } }}>
            Contacts
          </Typography>
        </Box>

        <Tabs
          value={view}
          onChange={(_, v: View) => {
            setTrashPreselect(undefined);
            setView(v);
          }}
          variant="scrollable"
          scrollButtons={false}
          sx={{
            minHeight: 60,
            '& .MuiTab-root': {
              minHeight: 60,
              px: { xs: 1, sm: 1.5 },
              minWidth: 0,
              fontSize: fs(15),
            },
          }}
        >
          <Tab value="contacts" label="Contacts" />
          <Tab value="insights" label="Insights" />
          <Tab value="trash" label={`Trash${trashed.length ? ` (${trashed.length})` : ''}`} />
          <Tab
            value="duplicates"
            label={
              <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 1 }}>
                Duplicates
                {duplicateGroups.length > 0 && (
                  <Box
                    component="span"
                    aria-label={`${duplicateGroups.length} groups`}
                    sx={{
                      fontSize: fs(12),
                      fontWeight: 700,
                      lineHeight: '18px',
                      minWidth: 18,
                      px: 0.75,
                      borderRadius: '9px',
                      bgcolor: tokens.attention,
                      color: '#1a1206',
                      fontVariantNumeric: 'tabular-nums',
                    }}
                  >
                    {duplicateGroups.length}
                  </Box>
                )}
              </Box>
            }
          />
        </Tabs>

        <Box sx={{ flex: 1 }} />

        {renderSearch(false)}

        {assistantInfo?.enabled && (
          <Tooltip title="Assistant">
            <Button
              onClick={() => setAssistantOpen((v) => !v)}
              startIcon={<AutoAwesome />}
              variant={assistantOpen ? 'contained' : 'outlined'}
              size="small"
              aria-pressed={assistantOpen}
              sx={{
                flexShrink: 0,
                minWidth: 0,
                px: { xs: 1, sm: 1.5 },
                '& .MuiButton-startIcon': { mr: { xs: 0, sm: 1 } },
              }}
            >
              <Box component="span" sx={{ display: { xs: 'none', sm: 'inline' } }}>
                Assistant
              </Box>
            </Button>
          </Tooltip>
        )}

        {onMobileDevice && (
          <Tooltip title="Phone sync">
            <IconButton onClick={() => setPhoneSyncOpen(true)} aria-label="Phone sync">
              <PhonelinkRing />
            </IconButton>
          </Tooltip>
        )}

        <Tooltip title="Reload from Google">
          <span>
            <IconButton onClick={load} disabled={loading} aria-label="Reload contacts">
              {loading ? <CircularProgress size={20} /> : <Refresh />}
            </IconButton>
          </span>
        </Tooltip>

        <IconButton
          onClick={(e) => setAccountAnchor(e.currentTarget)}
          aria-label="Account"
          size="small"
        >
          <Avatar
            src={user?.profilePicture}
            sx={{ width: 32, height: 32, fontSize: fs(15), bgcolor: tokens.accent, color: '#fff' }}
          >
            {userInitial}
          </Avatar>
        </IconButton>
        <Menu
          anchorEl={accountAnchor}
          open={!!accountAnchor}
          onClose={() => setAccountAnchor(null)}
        >
          <MenuItem disabled sx={{ opacity: '1 !important', display: 'block' }}>
            <Typography variant="body2" sx={{ fontWeight: 600 }}>
              {user?.name || 'Signed in with Google'}
            </Typography>
            {user?.email && (
              <Typography variant="caption" sx={{ color: tokens.textSecondary }}>
                {user.email}
              </Typography>
            )}
          </MenuItem>
          <MenuItem
            onClick={() => {
              setAccountAnchor(null);
              setPhoneSyncOpen(true);
            }}
          >
            <ListItemIcon>
              <PhonelinkRing fontSize="small" />
            </ListItemIcon>
            Phone sync
          </MenuItem>
          <MenuItem onClick={handleSignOut}>
            <ListItemIcon>
              <LogoutIcon fontSize="small" />
            </ListItemIcon>
            Sign out
          </MenuItem>
        </Menu>
        <Tooltip title="Sign out">
          <IconButton onClick={handleSignOut} aria-label="Sign out" size="small">
            <LogoutIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      </Box>

      {/* Phones: search on its own row */}
      <Box
        sx={{
          display: { xs: 'block', sm: 'none' },
          px: 1.5,
          py: 1,
          borderBottom: `1px solid ${tokens.border}`,
          bgcolor: tokens.surface,
        }}
      >
        {renderSearch(true)}
      </Box>

      {/* Views, with the assistant beside them */}
      <Box sx={{ flex: 1, minHeight: 0, display: 'flex' }}>
        <Box
          component="main"
          sx={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column' }}
        >
          {view === 'contacts' && renderContactsView()}
          {view === 'insights' && (
            <InsightsView
              stats={stats}
              onShowFilter={showFilter}
              onShowDuplicates={() => setView('duplicates')}
              orgInsights={orgInsights}
              onReviewDomain={setReviewDomain}
              activitySection={
                <>
                  <FavouritesCard
                    contacts={contacts}
                    activity={activityIndex ?? null}
                    onOpenCard={setCardFor}
                    onShowFavourites={() => showFilter('favourites')}
                  />
                  <ReachOutSection
                    contacts={contacts}
                    index={activityIndex}
                    threads={activity.threads}
                    connected={activity.connected}
                    onOpenCard={setCardFor}
                    onDraft={(text) => {
                      setAssistantOpen(true);
                      setAssistantRequest({ text, id: Date.now() });
                    }}
                    onSuggestEmails={() => setSuggestOpen(true)}
                    onSignatures={() => setSignatureTargets('auto')}
                    account={user?.email}
                    withoutEmail={filterCounts.noEmail ?? 0}
                    onMarkOwn={(emails) => {
                      void backendApiClient
                        .markOwnEmails(emails)
                        .then(() => activity.reload())
                        .catch((e) =>
                          logger.warn(
                            {
                              context: 'ContactsHome/markOwn',
                              metadata: { message: (e as Error).message },
                            },
                            'Marking own address failed',
                          ),
                        );
                    }}
                  />
                  <ActivitySection
                    activity={activity}
                    contacts={contacts}
                    allContacts={allContacts}
                    index={activityIndex}
                    onOpenCard={setCardFor}
                    onShowContacts={showContacts}
                    onAddContact={addContactForEmail}
                  />
                  <ProfilesCard contacts={contacts} profiles={profiles} onOpenCard={setCardFor} />
                  <SuggestedSegments
                    contacts={contacts}
                    ctx={segmentCtx}
                    segments={segments}
                    groups={groups}
                    onSave={segmentsState.save}
                    onShow={(members, title) =>
                      showContacts(
                        members.map((m) => m.resourceName),
                        title,
                      )
                    }
                  />
                </>
              }
            />
          )}
          {view === 'trash' && (
            <TrashView
              key={trashPreselect?.join(',') ?? 'all'}
              trashed={trashed}
              preselect={trashPreselect}
              onRestore={restore}
              onDeleteForever={(list) => {
                setDeletePermanent(true);
                setDeleteTargets(list);
              }}
            />
          )}
          {view === 'duplicates' && (
            <DuplicatesView
              groups={duplicateGroups}
              onOpenCard={setCardFor}
              onMerge={setMergeTargets}
              onDelete={(c) => setDeleteTargets([c])}
            />
          )}
        </Box>
        {assistantInfo?.enabled && (
          <AssistantPanel
            open={assistantOpen}
            onClose={() => setAssistantOpen(false)}
            model={assistantInfo.model}
            request={assistantRequest}
            entities={entities}
            groups={groups}
            context={assistantContext}
          />
        )}
      </Box>

      <ContactCardDrawer
        resourceName={cardFor}
        groups={groups}
        onClose={() => setCardFor(null)}
        onDelete={(c) => setDeleteTargets([c])}
        activity={activityIndex}
        onReadSignature={(c) => setSignatureTargets([c])}
        dossier={dossier}
      />

      <OrgSuggestionsDialog
        insight={reviewDomain}
        onClose={() => setReviewDomain(null)}
        onApplied={(count) => {
          setReviewDomain(null);
          setToast(`Organization updated for ${count} ${count === 1 ? 'contact' : 'contacts'}`);
        }}
      />
      <DeleteDialog
        contacts={deleteTargets}
        permanentOnly={deletePermanent}
        onClose={() => {
          setDeleteTargets(null);
          setDeletePermanent(false);
        }}
        onDeleted={(count, permanent) => {
          setDeletePermanent(false);
          handleDeleted(count, permanent);
        }}
      />
      <SignatureDialog
        targets={signatureTargets}
        onClose={() => setSignatureTargets(null)}
        contacts={contacts}
        groups={groups}
        index={activityIndex}
        connected={!!activity.status?.connected.signatures}
        onApplied={(count) =>
          count > 0 && setToast(`Updated ${count} contacts from their signatures`)
        }
      />
      <SegmentEditor
        segment={editingSegment}
        contacts={contacts}
        groups={groups}
        ctx={segmentCtx}
        onSave={async (s) => {
          await segmentsState.save(s);
          setSegmentId(s.id);
        }}
        onDelete={async (id) => {
          await segmentsState.remove(id);
          if (segmentId === id) {
            setSegmentId(null);
          }
        }}
        onClose={() => setEditingSegment(null)}
      />
      <LabelsManager open={labelsOpen} groups={groups} onClose={() => setLabelsOpen(false)} />
      {updateFor?.label && groups[updateFor.label] && (
        <UpdateLabelDialog
          open
          label={updateFor.label}
          labelName={groups[updateFor.label]?.name ?? 'label'}
          {...labelDiff(segmentMembers.get(updateFor.id) ?? [], contacts, updateFor.label)}
          onClose={() => setUpdateFor(null)}
        />
      )}
      <SuggestEmailsDialog
        open={suggestOpen}
        onClose={() => setSuggestOpen(false)}
        contacts={contacts}
        allContacts={allContacts}
        groups={groups}
        onApplied={(count) => {
          if (count > 0) {
            setToast(`Added ${count} emails. Re-reading mail so their history appears…`);
            // Their past mail is attributed only by a full scan
            void activity.refresh(true);
          }
        }}
      />
      <DeviceSyncDialog
        open={phoneSyncOpen}
        onClose={() => setPhoneSyncOpen(false)}
        allContacts={allContacts}
        groups={groups}
        viewContacts={visible}
        onDone={setToast}
      />
      <MergeDialog
        contacts={mergeTargets}
        groups={groups}
        onClose={() => setMergeTargets(null)}
        onMerged={handleMerged}
      />
      <Snackbar
        open={!!toast}
        autoHideDuration={4000}
        onClose={() => setToast(null)}
        message={toast}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
      />

      {/* Column Configuration Dialog */}
      <ColumnConfigDialog open={isColumnConfigOpen} onClose={() => setIsColumnConfigOpen(false)} />

      {/* Story 3.7: Conflict Resolution Dialog */}
      {firstConflict && (
        <ConflictResolutionDialog
          operation={firstConflict}
          remoteValue={firstConflict.remoteValue}
          onKeepMine={handleKeepMine}
          onUseRemote={handleUseRemote}
        />
      )}
    </Box>
  );
};

/**
 * Home: the contacts workspace when signed in, otherwise the same page with a
 * sign-in button (there is no separate login page).
 * @returns Page element
 */
const ContactsHome: React.FC = () => {
  const isAuthenticated = useSelector(selectIsAuthenticated);
  return isAuthenticated ? <SignedInHome /> : <SignedOutHome />;
};

/** The Contacts area (signed in), used by the site shell */
export const ContactsWorkspace = SignedInHome;

export default ContactsHome;
