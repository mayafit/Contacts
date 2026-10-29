/**
 * @fileoverview The Activity panel's route line (story 12.7): the quality or the match
 * in a few words, a compact Roads / Recorded toggle, "Revert to recorded", and "Fix this
 * route" with its progress. Hidden when route quality is off.
 * @module Contacts/atlas/RouteLine
 */

import React, { useEffect, useRef, useState } from 'react';
import { CircularProgress, Link, ToggleButton, ToggleButtonGroup, Typography } from '@mui/material';
import { ApiRequestError, backendApiClient } from '../../../services/api/BackendApiClient';
import type { ActivityDetail, RouteJob } from './types';
import { jobText, routeCaption } from './routeGeometry';
import { tokens } from '../theme/contactsTheme';

/** How the open activity is drawn */
export type RouteView = 'roads' | 'recorded';

/** Props for RouteLine */
export interface RouteLineProps {
  detail: Pick<ActivityDetail, 'summary' | 'route'>;
  /** Roads (the matched route) or Recorded */
  view: RouteView;
  onView: (view: RouteView) => void;
  /** Loads the activity again after a route change; resolves to it (null when gone) */
  onChanged: () => Promise<Pick<ActivityDetail, 'summary'> | null>;
}

/** Polls a route job this often (ms) */
export const POLL_MS = 2000;

/** Gives up waiting for a job after this long (ms) */
const WAIT_MS = 120_000;

/** A link that looks like the caption around it (as PhotosLink) */
const InlineLink: React.FC<{
  onClick: () => void;
  disabled?: boolean;
  children: React.ReactNode;
}> = ({ onClick, disabled, children }) => (
  <Link
    component="button"
    type="button"
    variant="inherit"
    underline="hover"
    disabled={disabled}
    onClick={onClick}
    sx={{ color: 'inherit', fontSize: 'inherit', verticalAlign: 'baseline', whiteSpace: 'nowrap' }}
  >
    {children}
  </Link>
);

/** The note shown when a route call fails */
const failureText = (e: unknown): string =>
  e instanceof ApiRequestError && e.code === 'ROUTES_UNAVAILABLE'
    ? 'Route matching unavailable'
    : 'That did not work; try again later';

/**
 * The route line under the panel's header.
 * @param props - Component props
 * @returns The line, or nothing when route quality is off
 */
const RouteLine: React.FC<RouteLineProps> = ({ detail, view, onView, onChanged }) => {
  const s = detail.summary;
  const route = detail.route;
  const [job, setJob] = useState<RouteJob | null>(null);
  const [working, setWorking] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const alive = useRef(true);
  useEffect(
    () => () => {
      alive.current = false;
    },
    [],
  );

  const caption = routeCaption(s, route);
  if (!caption || !route) {
    return null;
  }

  /** Waits for a job to finish (or until it is no use waiting) */
  const follow = async (jobId: string): Promise<RouteJob | null> => {
    const started = Date.now();
    while (alive.current && Date.now() - started < WAIT_MS) {
      await new Promise((r) => setTimeout(r, POLL_MS));
      if (!alive.current) {
        return null;
      }
      const j = await backendApiClient.routesJob(jobId);
      if (alive.current) {
        setJob(j);
      }
      if (j.state === 'done' || j.state === 'failed') {
        return j;
      }
    }
    return null;
  };

  const fix = async () => {
    setWorking(true);
    setNote(null);
    setJob(null);
    try {
      if (caption.rejected) {
        // "Try again" forgets "Keep the original" first
        await backendApiClient.routesClearDecision(s.id);
      }
      const { jobId } = await backendApiClient.routesImprove(route.recordIds);
      setJob({
        id: jobId,
        kind: 'improve',
        priority: 'interactive',
        state: 'queued',
        total: 1,
        done: 0,
      });
      const finished = await follow(jobId);
      if (!alive.current) {
        return;
      }
      const after = await onChanged();
      if (!alive.current) {
        return;
      }
      if (!finished) {
        setNote('Still waiting for the route service; check again later');
      } else if (finished.state === 'failed') {
        setNote('Matching failed');
      } else if (after?.summary.geometry !== 'matched') {
        setNote('No confident road match');
      } else {
        onView('roads');
      }
    } catch (e) {
      if (alive.current) {
        setNote(failureText(e));
      }
    } finally {
      if (alive.current) {
        setWorking(false);
        setJob(null);
      }
    }
  };

  const revert = async () => {
    setWorking(true);
    setNote(null);
    try {
      await backendApiClient.routesDecide(s.id, 'reject');
      await onChanged();
    } catch (e) {
      if (alive.current) {
        setNote(failureText(e));
      }
    } finally {
      if (alive.current) {
        setWorking(false);
      }
    }
  };

  return (
    <Typography
      variant="caption"
      component="div"
      sx={{
        color: tokens.textMuted,
        ml: 5,
        display: 'flex',
        alignItems: 'center',
        flexWrap: 'wrap',
        columnGap: 0.75,
        rowGap: 0.25,
      }}
    >
      <span title={caption.hint}>{caption.text}</span>
      {caption.matched && (
        <ToggleButtonGroup
          size="small"
          exclusive
          value={view}
          onChange={(_, v: RouteView | null) => v && onView(v)}
          aria-label="Draw the route"
          sx={{
            '& .MuiToggleButton-root': {
              py: 0,
              px: 0.6,
              fontSize: 11,
              height: 18,
              textTransform: 'none',
            },
          }}
        >
          <ToggleButton value="roads">Roads</ToggleButton>
          <ToggleButton value="recorded">Recorded</ToggleButton>
        </ToggleButtonGroup>
      )}
      {caption.matched && !working && (
        <InlineLink onClick={() => void revert()}>Revert to recorded</InlineLink>
      )}
      {working && job ? (
        <span role="status" style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
          <CircularProgress size={10} aria-label="Matching the route" />
          {jobText(job)}
        </span>
      ) : (
        caption.canFix &&
        !working && (
          <InlineLink onClick={() => void fix()}>
            {caption.rejected ? 'Try again' : 'Fix this route'}
          </InlineLink>
        )
      )}
      {working && !job && <CircularProgress size={10} aria-label="Saving" />}
      {note && <span role="status">{note}</span>}
    </Typography>
  );
};

export default RouteLine;
