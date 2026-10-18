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
} from '@mui/material';
import {
  CallMerge,
  Close,
  ContactPhone,
  DeleteOutline,
  Logout as LogoutIcon,
  Refresh,
  Search,
  ViewColumn,
} from '@mui/icons-material';
import { logout } from '../redux/slices/auth/authSlice';
import { selectUser } from '../redux/slices/auth/selectors';
import { selectConflictedOperations } from '../redux/slices/syncQueue/selectors';
import { operationSuccess, operationFailed } from '../redux/slices/syncQueue/syncQueueSlice';
import {
  contactUpdated,
  fetchContactGroups,
  fetchContacts,
} from '../redux/slices/contacts/contactsSlice';
import {
  selectAllContacts,
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
import { useAppSelector, useAppDispatch } from '../types/hooks';
import type { Contact, ContactGroup } from '../types/Contact';
import { getPhoneticName, hasName, matchesQuery, searchTextOf } from '../model/contactView';
import { duplicateMemberSet, findDuplicateGroups } from '../model/duplicates';
import { computeContactStats, type ContactFilter } from '../model/stats';
import { tokens } from '../theme/contactsTheme';

type View = 'contacts' | 'insights' | 'duplicates';

const FILTERS: { id: ContactFilter; label: string }[] = [
  { id: 'all', label: 'All' },
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
const ContactsHome: React.FC = () => {
  const dispatch = useAppDispatch();
  const navigate = useNavigate();
  const user = useSelector(selectUser);
  const contacts = useSelector(selectAllContacts) as Contact[];
  const loading = useSelector(selectContactsLoading);
  const error = useSelector(selectContactsError);
  const groups = useAppSelector(selectContactGroups) as Record<string, ContactGroup>;

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
  const [toast, setToast] = useState<string | null>(null);
  const [isColumnConfigOpen, setIsColumnConfigOpen] = useState(false);
  const [accountAnchor, setAccountAnchor] = useState<HTMLElement | null>(null);
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
  const searchIndex = useMemo(
    () => new Map(contacts.map((c) => [c.resourceName, searchTextOf(c)])),
    [contacts],
  );

  const predicates = useMemo<Record<ContactFilter, (c: Contact) => boolean>>(
    () => ({
      all: () => true,
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
          (!label || (c.labels ?? []).includes(label)) &&
          matchesQuery(c, deferredQuery, searchIndex.get(c.resourceName)),
      ),
    [contacts, predicates, filter, label, deferredQuery, searchIndex],
  );

  const showFilter = (f: ContactFilter) => {
    setFilter(f);
    setLabel('');
    setQuery('');
    setView('contacts');
  };

  const handleSignOut = () => {
    logger.info({ context: 'ContactsHome/handleSignOut' }, 'User initiated sign out');
    setAccountAnchor(null);
    dispatch(logout());
    navigate('/login');
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

  const handleDeleted = (count: number) => {
    const removed = (deleteTargets ?? []).map((c) => c.resourceName);
    setDeleteTargets(null);
    forget(removed);
    setToast(count === 1 ? 'Contact deleted' : `${count} contacts deleted`);
  };

  const handleMerged = (kept: Contact, removed: number) => {
    const removedNames = (mergeTargets ?? [])
      .map((c) => c.resourceName)
      .filter((rn) => rn !== kept.resourceName);
    setMergeTargets(null);
    forget(removedNames);
    setChecked(new Set());
    setToast(`Merged ${removed + 1} contacts into one`);
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
        {labelOptions.length > 0 && (
          <Select
            size="small"
            value={label}
            displayEmpty
            onChange={(e) => setLabel(e.target.value)}
            aria-label="Filter by label"
            sx={{ minWidth: 150, height: 32, fontSize: 13, borderRadius: '8px' }}
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
          </Box>
        ) : (
          <ContactsTable
            contacts={visible}
            groups={groups}
            selectedResourceName={cardFor}
            onOpenCard={setCardFor}
            scrollResetKey={`${filter}|${label}|${deferredQuery}`}
            checked={checked}
            onCheckedChange={setChecked}
          />
        )}
      </Paper>
      <Typography variant="caption" sx={{ color: tokens.textMuted, textAlign: 'center' }}>
        Click a cell to edit · Double-click a row for the full card · Enter saves and moves down ·
        Tab moves right · Esc cancels
      </Typography>
    </Box>
  );

  return (
    <Box
      sx={{
        height: '100vh',
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
            <ContactPhone sx={{ fontSize: 18, color: '#fff' }} />
          </Box>
          <Typography variant="h3" sx={{ display: { xs: 'none', sm: 'block' } }}>
            Contacts
          </Typography>
        </Box>

        <Tabs
          value={view}
          onChange={(_, v: View) => setView(v)}
          variant="scrollable"
          scrollButtons={false}
          sx={{
            minHeight: 60,
            '& .MuiTab-root': { minHeight: 60, px: 1.5, minWidth: 0, fontSize: 14 },
          }}
        >
          <Tab value="contacts" label="Contacts" />
          <Tab value="insights" label="Insights" />
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
                      fontSize: 11,
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

        <Box
          sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 1,
            px: 1.5,
            height: 38,
            width: { xs: 110, sm: 260, md: 380 },
            flexShrink: 1,
            minWidth: 0,
            borderRadius: '10px',
            bgcolor: tokens.page,
            border: `1px solid ${tokens.border}`,
            '&:focus-within': { borderColor: tokens.accent },
          }}
        >
          <Search sx={{ fontSize: 20, color: tokens.textMuted }} />
          <InputBase
            inputRef={searchRef}
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setView('contacts');
            }}
            onKeyDown={(e) => e.key === 'Escape' && setQuery('')}
            placeholder="Search name, phonetic, phone, email…"
            inputProps={{ 'aria-label': 'Search contacts', dir: 'auto' }}
            sx={{ flex: 1, fontSize: 14 }}
          />
          {query ? (
            <IconButton size="small" onClick={() => setQuery('')} aria-label="Clear search">
              <Close sx={{ fontSize: 16 }} />
            </IconButton>
          ) : (
            <Box
              component="kbd"
              sx={{
                fontSize: 11,
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
            sx={{ width: 32, height: 32, fontSize: 14, bgcolor: tokens.raised }}
          >
            {userInitial}
          </Avatar>
        </IconButton>
        <Menu
          anchorEl={accountAnchor}
          open={!!accountAnchor}
          onClose={() => setAccountAnchor(null)}
        >
          <MenuItem disabled sx={{ opacity: '1 !important' }}>
            <Typography variant="body2" sx={{ color: tokens.textSecondary }}>
              Signed in with Google
            </Typography>
          </MenuItem>
          <MenuItem onClick={handleSignOut}>
            <ListItemIcon>
              <LogoutIcon fontSize="small" />
            </ListItemIcon>
            Sign out
          </MenuItem>
        </Menu>
      </Box>

      {/* Views */}
      <Box
        component="main"
        sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}
      >
        {view === 'contacts' && renderContactsView()}
        {view === 'insights' && (
          <InsightsView
            stats={stats}
            onShowFilter={showFilter}
            onShowDuplicates={() => setView('duplicates')}
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

      <ContactCardDrawer
        resourceName={cardFor}
        groups={groups}
        onClose={() => setCardFor(null)}
        onDelete={(c) => setDeleteTargets([c])}
      />

      <DeleteDialog
        contacts={deleteTargets}
        onClose={() => setDeleteTargets(null)}
        onDeleted={handleDeleted}
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

export default ContactsHome;
