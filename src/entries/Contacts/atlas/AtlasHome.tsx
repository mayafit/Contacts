/**
 * @fileoverview Atlas: world map, timeline, results, details, import, AI access and assistant
 * @module Contacts/atlas/AtlasHome
 */

import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  IconButton,
  InputBase,
  Paper,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  AutoAwesome,
  Close,
  CropFree,
  Hub,
  Map as MapIcon,
  MergeType,
  Place,
  Search,
  UploadFile,
} from '@mui/icons-material';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import type { ActivityDetail, AtlasQuery, AtlasSummary, SearchResult, VisitSummary } from './types';
import AtlasMap, { type Box as Area } from './AtlasMap';
import AtlasTimeline, { type Range } from './AtlasTimeline';
import ResultsList from './ResultsList';
import ActivityPanel from './ActivityPanel';
import ImportDialog from './ImportDialog';
import AiAccessDialog from './AiAccessDialog';
import FusionSettingsDialog from './FusionSettingsDialog';
import AtlasFilterBar from './AtlasFilterBar';
import AssistantPanel from '../components/assistant/AssistantPanel';
import { atlasToolset } from './atlasTools';
import { formatDuration, formatWhen } from './geo';
import { tokens } from '../theme/contactsTheme';
import { VISIT_COLOR, isSet } from './types';

const YEAR = 365 * 86_400_000;

const SUGGESTIONS = [
  'What did I do last weekend?',
  'Show all my bike rides this year on the map',
  'What was my heart rate during my longest ride?',
  'When was I last in Haifa, and did I ride there?',
];

/**
 * Atlas page.
 * @returns Page element
 */
const AtlasHome: React.FC = () => {
  const [status, setStatus] = useState<{
    configured: boolean;
    ready: boolean;
    failed?: string;
  } | null>(null);
  const [summary, setSummary] = useState<AtlasSummary | null>(null);
  const [view, setView] = useState<Range>({
    from: Date.now() - YEAR,
    to: Date.now() + 7 * 86_400_000,
  });
  const [selection, setSelection] = useState<Range | null>(null);
  const [area, setArea] = useState<Area | null>(null);
  const [drawing, setDrawing] = useState(false);
  const [types, setTypes] = useState<string[]>([]);
  const [text, setText] = useState('');
  const [query, setQuery] = useState('');
  const [result, setResult] = useState<SearchResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [detail, setDetail] = useState<ActivityDetail | null>(null);
  const [visit, setVisit] = useState<VisitSummary | null>(null);
  const [cursor, setCursor] = useState<[number, number] | null>(null);
  const [importOpen, setImportOpen] = useState(false);
  const [aiOpen, setAiOpen] = useState(false);
  const [fusionOpen, setFusionOpen] = useState(false);
  // Bumped to fit the map to the area filter
  const [focusArea, setFocusArea] = useState(0);
  const [assistantOpen, setAssistantOpen] = useState(false);
  const [assistantEnabled, setAssistantEnabled] = useState(false);
  const [reloadKey, setReloadKey] = useState(0);
  // The activity being merged while the user picks the other one (map or list)
  const [mergeFrom, setMergeFrom] = useState<string | null>(null);
  // Bumped when new results arrive, so the map fits to them (not to the old ones)
  const [fitTick, setFitTick] = useState(0);

  const loadSummary = useCallback(async () => {
    try {
      const s = await backendApiClient.atlasSummary();
      setSummary(s);
      return s;
    } catch {
      return null;
    }
  }, []);

  useEffect(() => {
    backendApiClient
      .atlasStatus()
      .then(async (st) => {
        setStatus(st);
        if (!st.ready) {
          return;
        }
        const s = await loadSummary();
        if (s?.first && s?.last) {
          const pad = Math.max(86_400_000, (Date.parse(s.last) - Date.parse(s.first)) * 0.03);
          setView({ from: Date.parse(s.first) - pad, to: Date.parse(s.last) + pad });
        }
      })
      .catch((e) =>
        setStatus({
          configured: true,
          ready: false,
          failed: (e as Error).message || 'Atlas could not be reached',
        }),
      );
    backendApiClient
      .getAssistantStatus()
      .then((a) => setAssistantEnabled(a.enabled))
      .catch(() => setAssistantEnabled(false));
  }, [loadSummary]);

  // Debounced text search
  useEffect(() => {
    const t = setTimeout(() => setQuery(text.trim()), 350);
    return () => clearTimeout(t);
  }, [text]);

  const q: AtlasQuery = useMemo(
    () => ({
      from: selection ? new Date(selection.from).toISOString() : undefined,
      to: selection ? new Date(selection.to).toISOString() : undefined,
      ...(area ?? {}),
      types: types.length ? types : undefined,
      text: query || undefined,
      limit: 500,
    }),
    [selection, area, types, query],
  );
  const fitKey = JSON.stringify(q);

  // The timeline counts what the map shows (area, types, text; its own time)
  const timelineFilter: AtlasQuery = useMemo(
    () => ({ ...(area ?? {}), types: types.length ? types : undefined, text: query || undefined }),
    [area, types, query],
  );

  // The selected activity or visit, marked on the timeline
  const highlight = useMemo(() => {
    const item = detail?.summary ?? visit;
    return item ? { from: Date.parse(item.startTime), to: Date.parse(item.endTime) } : null;
  }, [detail, visit]);

  // Bring the selected item into the timeline's view when it is outside it
  useEffect(() => {
    if (!highlight) {
      return;
    }
    setView((v) => {
      if (highlight.from >= v.from && highlight.to <= v.to) {
        return v;
      }
      const span = Math.max(v.to - v.from, (highlight.to - highlight.from) * 3);
      const mid = (highlight.from + highlight.to) / 2;
      return { from: mid - span / 2, to: mid + span / 2 };
    });
  }, [highlight]);

  useEffect(() => {
    if (!status?.ready) {
      return undefined;
    }
    let alive = true;
    setLoading(true);
    backendApiClient
      .atlasSearch(q)
      .then((r) => {
        if (alive) {
          setResult(r);
          setError(null);
          setFitTick((n) => n + 1);
        }
      })
      .catch((e) => alive && setError((e as Error).message))
      .finally(() => alive && setLoading(false));
    return () => {
      alive = false;
    };
  }, [fitKey, status?.ready, reloadKey]);

  const openActivity = useCallback(async (id: string) => {
    setVisit(null);
    try {
      setDetail(await backendApiClient.atlasActivity(id));
    } catch (e) {
      setError((e as Error).message);
    }
  }, []);

  /**
   * Merges the activity being merged with the picked one and opens the result.
   * @param from - The activity the merge started from
   * @param picked - The activity picked on the map or in the list
   */
  const mergeWith = useCallback(
    async (from: string, picked: string) => {
      if (from === picked) {
        setError('Pick a different activity to merge with.');
        return;
      }
      setMergeFrom(null);
      try {
        const merged = await backendApiClient.atlasMerge(from, picked);
        await openActivity(merged.summary.id);
        setReloadKey((k) => k + 1);
      } catch (e) {
        setError((e as Error).message || 'Merging failed');
      }
    },
    [openActivity],
  );

  /** A click on an activity (map or list): opens it, or picks it while merging */
  const selectActivity = useCallback(
    (id: string) => {
      if (mergeFrom) {
        void mergeWith(mergeFrom, id);
      } else {
        void openActivity(id);
      }
    },
    [mergeFrom, mergeWith, openActivity],
  );

  // Escape ends picking
  useEffect(() => {
    if (!mergeFrom) {
      return undefined;
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setMergeFrom(null);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [mergeFrom]);

  const showQuery = useCallback((s: AtlasQuery) => {
    setDetail(null);
    setVisit(null);
    setSelection(s.from && s.to ? { from: Date.parse(s.from), to: Date.parse(s.to) } : null);
    if (s.from && s.to) {
      const pad = (Date.parse(s.to) - Date.parse(s.from)) * 0.1 + 3_600_000;
      setView({ from: Date.parse(s.from) - pad, to: Date.parse(s.to) + pad });
    }
    setArea(
      isSet(s.minLat) && isSet(s.maxLat) && isSet(s.minLon) && isSet(s.maxLon)
        ? { minLat: s.minLat, minLon: s.minLon, maxLat: s.maxLat, maxLon: s.maxLon }
        : null,
    );
    setTypes(s.types ?? []);
    setText(s.text ?? '');
  }, []);

  const toolset = useMemo(
    () => atlasToolset({ show: showQuery, openActivity: (id) => void openActivity(id), summary }),
    [showQuery, openActivity, summary],
  );

  const changed = () => {
    setReloadKey((k) => k + 1);
    void loadSummary();
  };

  /** Removes every filter: time, area, types and text */
  const clearFilters = () => {
    setSelection(null);
    setArea(null);
    setDrawing(false);
    setTypes([]);
    setText('');
    setQuery('');
  };

  /** Zooms the timeline to the time filter */
  const zoomToSelection = () => {
    if (!selection) {
      return;
    }
    const pad = Math.max((selection.to - selection.from) * 0.1, 3_600_000);
    setView({ from: selection.from - pad, to: selection.to + pad });
  };

  const filtered = !!selection || !!area || types.length > 0 || query !== '';

  if (status && !status.ready) {
    return (
      <Box sx={{ p: 4 }}>
        <Alert severity={status.failed ? 'error' : 'info'}>
          {status.failed
            ? `Atlas could not be reached: ${status.failed}. Reload to try again.`
            : status.configured
              ? 'The Atlas database is starting. Reload in a minute.'
              : 'Atlas needs its database (Database__ConnectionString), which is not configured here.'}
        </Alert>
      </Box>
    );
  }

  const empty = summary && summary.activities + summary.visits + summary.healthSamples === 0;

  return (
    <Box
      sx={{
        flex: 1,
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
        bgcolor: tokens.page,
        color: tokens.text,
      }}
    >
      <Box
        component="header"
        sx={{
          display: 'flex',
          alignItems: 'center',
          gap: { xs: 0.75, md: 1.5 },
          px: { xs: 1.5, md: 2.5 },
          height: 60,
          flexShrink: 0,
          borderBottom: `1px solid ${tokens.border}`,
          bgcolor: tokens.surface,
        }}
      >
        <MapIcon sx={{ color: tokens.accent }} />
        <Typography variant="h3" sx={{ display: { xs: 'none', sm: 'block' } }}>
          Atlas
        </Typography>
        <Box
          sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 1,
            px: 1.5,
            height: 38,
            flex: 1,
            maxWidth: 420,
            minWidth: 0,
            ml: { md: 2 },
            borderRadius: '10px',
            bgcolor: tokens.page,
            border: `1px solid ${tokens.border}`,
            '&:focus-within': { borderColor: tokens.accent },
          }}
        >
          <Search sx={{ fontSize: 20, color: tokens.textMuted }} />
          <InputBase
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder="Search places and activities…"
            inputProps={{ 'aria-label': 'Search Atlas', dir: 'auto' }}
            sx={{ flex: 1, fontSize: { xs: 16, sm: 14 } }}
          />
          {text && (
            <IconButton size="small" onClick={() => setText('')} aria-label="Clear search">
              <Close sx={{ fontSize: 16 }} />
            </IconButton>
          )}
        </Box>
        <Tooltip title={area ? 'Clear the area' : 'Select an area: drag on the map'}>
          <Button
            size="small"
            variant={drawing || area ? 'contained' : 'outlined'}
            startIcon={<CropFree />}
            onClick={() => {
              if (area) {
                setArea(null);
                setDrawing(false);
              } else {
                setDrawing((d) => !d);
              }
            }}
            sx={{ flexShrink: 0, minWidth: 0 }}
          >
            <Box component="span" sx={{ display: { xs: 'none', md: 'inline' } }}>
              {area ? 'Clear area' : drawing ? 'Drag on the map' : 'Area'}
            </Box>
          </Button>
        </Tooltip>
        <Box sx={{ flex: 1, display: { xs: 'none', md: 'block' } }} />
        <Tooltip title="Import">
          <Button
            size="small"
            startIcon={<UploadFile />}
            onClick={() => setImportOpen(true)}
            sx={{ minWidth: 0 }}
          >
            <Box component="span" sx={{ display: { xs: 'none', md: 'inline' } }}>
              Import
            </Box>
          </Button>
        </Tooltip>
        <Tooltip title="How sources are combined">
          <Button
            size="small"
            startIcon={<MergeType />}
            onClick={() => setFusionOpen(true)}
            sx={{ minWidth: 0 }}
          >
            <Box component="span" sx={{ display: { xs: 'none', md: 'inline' } }}>
              Fusion
            </Box>
          </Button>
        </Tooltip>
        <Tooltip title="AI access (MCP)">
          <IconButton onClick={() => setAiOpen(true)} aria-label="AI access">
            <Hub />
          </IconButton>
        </Tooltip>
        {assistantEnabled && (
          <Tooltip title="Assistant">
            <Button
              size="small"
              variant={assistantOpen ? 'contained' : 'outlined'}
              startIcon={<AutoAwesome />}
              onClick={() => setAssistantOpen((v) => !v)}
              sx={{ minWidth: 0, flexShrink: 0 }}
            >
              <Box component="span" sx={{ display: { xs: 'none', sm: 'inline' } }}>
                Assistant
              </Box>
            </Button>
          </Tooltip>
        )}
      </Box>

      <AtlasFilterBar
        selection={selection}
        area={area}
        types={types}
        text={query}
        result={result}
        loading={loading}
        onClearTime={() => setSelection(null)}
        onFocusTime={zoomToSelection}
        onClearArea={() => {
          setArea(null);
          setDrawing(false);
        }}
        onFocusArea={() => setFocusArea((n) => n + 1)}
        onClearTypes={() => setTypes([])}
        onClearText={() => {
          setText('');
          setQuery('');
        }}
        onClearAll={clearFilters}
      />

      {error && (
        <Alert severity="error" onClose={() => setError(null)} sx={{ borderRadius: 0 }}>
          {error}
        </Alert>
      )}
      {mergeFrom && (
        <Alert
          severity="info"
          sx={{ borderRadius: 0 }}
          action={
            <Button color="inherit" size="small" onClick={() => setMergeFrom(null)}>
              Cancel
            </Button>
          }
        >
          Pick the activity to merge with — on the map or in the list
        </Alert>
      )}

      <Box sx={{ flex: 1, minHeight: 0, display: 'flex' }}>
        <Box
          sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: { xs: 'column', md: 'row' } }}
        >
          <Box
            sx={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column' }}
          >
            <Box sx={{ flex: 1, minHeight: { xs: 280, md: 0 }, position: 'relative' }}>
              <AtlasMap
                activities={result?.activities ?? []}
                visits={result?.visits ?? []}
                selectedTrack={detail?.points}
                selectedId={detail?.summary.id}
                selectedVisitId={visit?.id}
                area={area}
                drawing={drawing}
                onAreaDrawn={(b) => {
                  setArea(b);
                  setDrawing(false);
                }}
                onSelectActivity={selectActivity}
                onSelectVisit={(v) => {
                  if (mergeFrom) {
                    return;
                  }
                  setDetail(null);
                  setVisit(v);
                }}
                fitKey={String(fitTick)}
                cursor={cursor}
                focusArea={focusArea}
              />
              {empty && (
                <Paper
                  sx={{
                    position: 'absolute',
                    zIndex: 1000,
                    top: 16,
                    left: '50%',
                    transform: 'translateX(-50%)',
                    p: 2.5,
                    maxWidth: 440,
                    bgcolor: tokens.surface,
                  }}
                >
                  <Typography variant="subtitle1">Bring in your data</Typography>
                  <Typography variant="body2" sx={{ color: tokens.textSecondary, my: 1 }}>
                    Import your Google Timeline, Google Fit / Fitbit export and Sports Tracker
                    workouts to see your trips, rides, hikes and heart rate here, together.
                  </Typography>
                  <Button
                    variant="contained"
                    startIcon={<UploadFile />}
                    onClick={() => setImportOpen(true)}
                  >
                    Import
                  </Button>
                </Paper>
              )}
            </Box>
            <AtlasTimeline
              view={view}
              onViewChange={setView}
              selection={selection}
              onSelectionChange={setSelection}
              reloadKey={reloadKey}
              filter={timelineFilter}
              highlight={highlight}
            />
          </Box>
          <Box
            sx={{
              width: { xs: '100%', md: 360 },
              flexShrink: 0,
              borderLeft: { md: `1px solid ${tokens.border}` },
              borderTop: { xs: `1px solid ${tokens.border}`, md: 'none' },
              bgcolor: tokens.surface,
              display: 'flex',
              flexDirection: 'column',
              minHeight: 0,
              maxHeight: { xs: 360, md: 'none' },
              overflowY: 'auto',
            }}
          >
            {detail && !mergeFrom ? (
              <ActivityPanel
                key={detail.summary.id}
                detail={detail}
                onBack={() => {
                  setDetail(null);
                  setCursor(null);
                }}
                onCursor={setCursor}
                onSave={async (patch) => {
                  try {
                    await backendApiClient.atlasUpdateActivity(detail.summary.id, patch);
                  } catch (e) {
                    setError((e as Error).message || 'Saving failed');
                    throw e;
                  }
                  await openActivity(detail.summary.id);
                  setReloadKey((k) => k + 1);
                }}
                onDelete={async () => {
                  const n = detail.summary.memberCount ?? 1;
                  if (
                    !window.confirm(
                      n > 1
                        ? `Delete this activity and all ${n} source records it was fused from, with their tracks?`
                        : 'Delete this activity and its track?',
                    )
                  ) {
                    return;
                  }
                  try {
                    await backendApiClient.atlasDeleteActivity(detail.summary.id);
                    setDetail(null);
                    changed();
                  } catch (e) {
                    setError((e as Error).message || 'Deleting failed');
                  }
                }}
                onSplit={async (memberId) => {
                  try {
                    await backendApiClient.atlasSplit(detail.summary.id, memberId);
                  } catch (e) {
                    setError((e as Error).message || 'Splitting failed');
                    throw e;
                  }
                  await openActivity(detail.summary.id);
                  setReloadKey((k) => k + 1);
                }}
                onStartMerge={() => {
                  setVisit(null);
                  setMergeFrom(detail.summary.id);
                }}
                onShowPlace={(lat, lon, visitId) => {
                  const v = result?.visits.find((x) => x.id === visitId);
                  if (v) {
                    setDetail(null);
                    setCursor(null);
                    setVisit(v);
                  } else {
                    // Not in the results: mark it on the map
                    setCursor([lat, lon]);
                  }
                }}
              />
            ) : visit ? (
              <Box sx={{ p: 2 }}>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                  <IconButton
                    size="small"
                    onClick={() => setVisit(null)}
                    aria-label="Back to results"
                  >
                    <Close fontSize="small" />
                  </IconButton>
                  <Place sx={{ color: VISIT_COLOR }} />
                  <Typography variant="subtitle1" dir="auto">
                    {visit.placeName || 'Visit'}
                  </Typography>
                </Box>
                <Typography variant="body2" sx={{ color: tokens.textSecondary, mt: 1 }}>
                  {formatWhen(visit.startTime)} ·{' '}
                  {formatDuration((Date.parse(visit.endTime) - Date.parse(visit.startTime)) / 1000)}
                </Typography>
                {visit.address && (
                  <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                    {visit.address}
                  </Typography>
                )}
                <Button
                  size="small"
                  sx={{ mt: 1.5 }}
                  onClick={async () => {
                    try {
                      const near = await backendApiClient.atlasNear(visit.lat, visit.lon, 300);
                      setVisit(null);
                      setResult({
                        activities: near.activities,
                        visits: near.visits,
                        totalActivities: near.activities.length,
                        totalVisits: near.visits.length,
                      });
                      setFitTick((n) => n + 1);
                    } catch (e) {
                      setError((e as Error).message || 'Search failed');
                    }
                  }}
                >
                  Everything at this place
                </Button>
              </Box>
            ) : (
              <ResultsList
                result={result}
                loading={loading}
                types={types}
                onToggleType={(t) =>
                  setTypes((ts) => (ts.includes(t) ? ts.filter((x) => x !== t) : [...ts, t]))
                }
                onSelectActivity={selectActivity}
                onSelectVisit={(v) => {
                  if (!mergeFrom) {
                    setVisit(v);
                  }
                }}
                filtered={filtered}
                onClearFilters={clearFilters}
              />
            )}
          </Box>
        </Box>
        {assistantEnabled && (
          <AssistantPanel
            open={assistantOpen}
            onClose={() => setAssistantOpen(false)}
            toolset={toolset}
            suggestions={SUGGESTIONS}
            entities={{}}
            groups={{}}
            onAtlas={(ref) => {
              const [kind, id] = ref.split('/');
              if (kind === 'activity' && id) {
                void openActivity(id);
              }
            }}
            privacyNote="Messages and the Atlas data the assistant looks up are sent to Google Gemini."
          />
        )}
      </Box>

      <ImportDialog open={importOpen} onClose={() => setImportOpen(false)} onChanged={changed} />
      <AiAccessDialog open={aiOpen} onClose={() => setAiOpen(false)} />
      <FusionSettingsDialog
        open={fusionOpen}
        onClose={() => setFusionOpen(false)}
        onChanged={() => {
          changed();
          if (detail) {
            // Its members and evidence may have changed; gone when re-fused into another
            backendApiClient
              .atlasActivity(detail.summary.id)
              .then(setDetail)
              .catch(() => setDetail(null));
          }
        }}
      />
    </Box>
  );
};

export default AtlasHome;
