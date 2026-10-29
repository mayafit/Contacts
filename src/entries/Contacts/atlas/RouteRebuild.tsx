/**
 * @fileoverview Rebuilding the open activity's route on demand (AD-38): from similar
 * drives, or through points the Owner marks on the map. The result is a suggestion,
 * drawn on the map, that the Owner uses or discards. Hidden when route quality is off.
 * @module Contacts/atlas/RouteRebuild
 */

import React, { useEffect, useRef, useState } from 'react';
import { Box, Button, CircularProgress, Link, Typography } from '@mui/material';
import { ApiRequestError, backendApiClient } from '../../../services/api/BackendApiClient';
import type { ActivityDetail, RebuildSuggestion, RouteJob } from './types';
import { noSuggestionText, rebuildText, suggestionVerdict } from './routeGeometry';
import { POLL_MS } from './RouteLine';
import { tokens } from '../theme/contactsTheme';

/** A suggestion of the open activity, and the job it came from */
export interface RebuildState {
  activityId: string;
  jobId: string;
  result: RebuildSuggestion;
}

/** Props for RouteRebuild */
export interface RouteRebuildProps {
  detail: Pick<ActivityDetail, 'summary' | 'route'>;
  /** The suggestion shown on the map, null for none */
  suggestion: RebuildState | null;
  onSuggestion: (s: RebuildState | null) => void;
  /** The points being marked on the map, null when not marking */
  marking: [number, number][] | null;
  onMarking: (points: [number, number][] | null) => void;
  /** Loads the activity again after the route changed */
  onApplied: () => Promise<unknown>;
}

/** Most points the Owner can mark (as the service) */
export const MAX_MARKS = 50;

/** Gives up waiting for a rebuild after this long (ms) */
const WAIT_MS = 120_000;

/** A link that looks like the caption around it */
const InlineLink: React.FC<{ onClick: () => void; children: React.ReactNode }> = ({
  onClick,
  children,
}) => (
  <Link
    component="button"
    type="button"
    variant="inherit"
    underline="hover"
    onClick={onClick}
    sx={{ color: 'inherit', fontSize: 'inherit', verticalAlign: 'baseline', whiteSpace: 'nowrap' }}
  >
    {children}
  </Link>
);

const failureText = (e: unknown): string =>
  e instanceof ApiRequestError && e.code === 'ROUTES_UNAVAILABLE'
    ? 'Route matching unavailable'
    : e instanceof ApiRequestError && e.code === 'CONFLICT'
      ? e.message
      : e instanceof ApiRequestError && e.code === 'INVALID_INPUT'
        ? e.message
        : 'That did not work; try again later';

/**
 * The rebuild line under the route line, the marking bar, or the suggestion card.
 * @param props - Component props
 * @returns The element, or nothing when the activity cannot be rebuilt
 */
const RouteRebuild: React.FC<RouteRebuildProps> = ({
  detail,
  suggestion,
  onSuggestion,
  marking,
  onMarking,
  onApplied,
}) => {
  const s = detail.summary;
  const route = detail.route;
  const [working, setWorking] = useState<'similar' | 'waypoints' | 'apply' | 'clear' | null>(null);
  const [note, setNote] = useState<RebuildSuggestion | string | null>(null);
  const alive = useRef(true);
  useEffect(
    () => () => {
      alive.current = false;
    },
    [],
  );

  // One recording, assessed as matchable: the service refuses the rest
  if (!route || route.recordIds.length !== 1 || (route.description?.matchable ?? 'yes') !== 'yes') {
    return null;
  }

  const follow = async (jobId: string): Promise<RouteJob | null> => {
    const started = Date.now();
    while (alive.current && Date.now() - started < WAIT_MS) {
      await new Promise((r) => setTimeout(r, POLL_MS));
      if (!alive.current) {
        return null;
      }
      const j = await backendApiClient.routesJob(jobId);
      if (j.state === 'done' || j.state === 'failed') {
        return j;
      }
    }
    return null;
  };

  const rebuild = async (method: 'similar' | 'waypoints', points?: [number, number][]) => {
    setWorking(method);
    setNote(null);
    onSuggestion(null);
    try {
      const { jobId } = await backendApiClient.routesRebuild(s.id, method, points);
      const job = await follow(jobId);
      if (!alive.current) {
        return;
      }
      if (!job) {
        setNote('Still waiting for the route service; try again later');
      } else if (job.state === 'failed' || !job.result) {
        setNote('Rebuilding failed');
      } else if (job.result.outcome === 'suggested' && job.result.polyline) {
        onMarking(null);
        onSuggestion({ activityId: s.id, jobId, result: job.result });
      } else {
        setNote(job.result);
      }
    } catch (e) {
      if (alive.current) {
        setNote(failureText(e));
      }
    } finally {
      if (alive.current) {
        setWorking(null);
      }
    }
  };

  const apply = async () => {
    if (!suggestion) {
      return;
    }
    setWorking('apply');
    setNote(null);
    try {
      await backendApiClient.routesApply(s.id, suggestion.jobId);
      onSuggestion(null);
      await onApplied();
    } catch (e) {
      if (alive.current) {
        setNote(failureText(e));
      }
    } finally {
      if (alive.current) {
        setWorking(null);
      }
    }
  };

  const clearMarks = async () => {
    setWorking('clear');
    setNote(null);
    try {
      await backendApiClient.routesClearWaypoints(s.id);
      await onApplied();
      if (alive.current) {
        setNote('Marked points cleared; the route is matched again shortly');
      }
    } catch (e) {
      if (alive.current) {
        setNote(failureText(e));
      }
    } finally {
      if (alive.current) {
        setWorking(null);
      }
    }
  };

  const caption = { color: tokens.textMuted, ml: 5, mt: 0.25 } as const;
  const startMarking = () => {
    setNote(null);
    onSuggestion(null);
    onMarking(route.description?.waypoints?.slice(0, MAX_MARKS) ?? []);
  };

  if (marking) {
    return (
      <Box
        role="group"
        aria-label="Mark the way"
        sx={{ ...caption, display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 0.75 }}
      >
        <Typography variant="caption" component="span">
          Click on the map where you drove, in order; click a point to remove it. {marking.length}{' '}
          of {MAX_MARKS} points
        </Typography>
        <Button
          size="small"
          variant="contained"
          disabled={marking.length === 0 || working !== null}
          onClick={() => void rebuild('waypoints', marking)}
          sx={{ py: 0, minHeight: 22 }}
        >
          Rebuild
        </Button>
        <Button
          size="small"
          onClick={() => onMarking(null)}
          disabled={working !== null}
          sx={{ py: 0, minHeight: 22 }}
        >
          Cancel
        </Button>
        {working && <CircularProgress size={10} aria-label="Rebuilding the route" />}
        {note && (
          <span role="status">{typeof note === 'string' ? note : noSuggestionText(note)}</span>
        )}
      </Box>
    );
  }

  if (suggestion && suggestion.activityId === s.id) {
    const r = suggestion.result;
    return (
      <Box
        role="group"
        aria-label="Suggested route"
        sx={{
          ml: 5,
          mt: 0.75,
          p: 1,
          border: `1px solid ${tokens.border}`,
          borderRadius: 1,
          bgcolor: tokens.surface,
        }}
      >
        <Typography variant="caption" component="div" sx={{ fontWeight: 600, color: tokens.text }}>
          Suggested route (drawn on the map)
        </Typography>
        <Typography variant="caption" component="div" sx={{ color: tokens.textSecondary }}>
          {rebuildText(r)}
        </Typography>
        <Typography variant="caption" component="div" sx={{ color: tokens.textMuted }}>
          {suggestionVerdict(r)}
        </Typography>
        <Box sx={{ display: 'flex', gap: 1, mt: 0.75, alignItems: 'center' }}>
          <Button
            size="small"
            variant="contained"
            onClick={() => void apply()}
            disabled={working !== null}
          >
            Use this route
          </Button>
          <Button size="small" onClick={() => onSuggestion(null)} disabled={working !== null}>
            Discard
          </Button>
          {working === 'apply' && <CircularProgress size={12} aria-label="Saving" />}
          {typeof note === 'string' && (
            <Typography variant="caption" role="status" sx={{ color: tokens.textMuted }}>
              {note}
            </Typography>
          )}
        </Box>
      </Box>
    );
  }

  const marked = route.description?.waypoints?.length ?? 0;
  return (
    <Typography
      variant="caption"
      component="div"
      sx={{
        ...caption,
        display: 'flex',
        alignItems: 'center',
        flexWrap: 'wrap',
        columnGap: 0.75,
        rowGap: 0.25,
      }}
    >
      {working ? (
        <span role="status" style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
          <CircularProgress size={10} aria-label="Rebuilding the route" />
          {working === 'similar'
            ? 'Looking for similar drives…'
            : working === 'clear'
              ? 'Clearing…'
              : 'Rebuilding…'}
        </span>
      ) : (
        <>
          <span>Rebuild:</span>
          <InlineLink onClick={() => void rebuild('similar')}>from similar drives</InlineLink>
          <span aria-hidden>·</span>
          <InlineLink onClick={startMarking}>
            {marked > 0 ? `change the ${marked} marked points` : 'mark the way'}
          </InlineLink>
          {marked > 0 && (
            <>
              <span aria-hidden>·</span>
              <InlineLink onClick={() => void clearMarks()}>clear them</InlineLink>
            </>
          )}
        </>
      )}
      {note && (
        <span role="status">{typeof note === 'string' ? note : noSuggestionText(note)}</span>
      )}
    </Typography>
  );
};

export default RouteRebuild;
