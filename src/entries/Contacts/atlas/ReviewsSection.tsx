/**
 * @fileoverview The Insights Reviews section (AD-39): the Owner's Google Maps reviews and
 * the places they went to and have not reviewed, all time
 * @module Contacts/atlas/ReviewsSection
 */

import React, { useEffect, useState } from 'react';
import { Box, ButtonBase, IconButton, Link, Menu, MenuItem, Typography } from '@mui/material';
import { MoreVert, RateReview } from '@mui/icons-material';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import type { ReviewAction, ReviewDue, ReviewsOverview } from './types';
import { Stars } from './PlaceReview';
import { dueText, overviewText, reviewedText, safeUrl } from './reviewText';
import { tokens } from '../theme/contactsTheme';

export interface ReviewsSectionProps {
  /** Selects a place on the map */
  onShowPlace: (lat: number, lon: number) => void;
}

const rowSx = {
  display: 'flex',
  alignItems: 'flex-start',
  gap: 1,
  width: '100%',
  textAlign: 'left',
  py: 0.75,
  borderBottom: `1px solid ${tokens.border}`,
} as const;

/** The choices on a reminder, as the menu lists them */
const CHOICES: { action: ReviewAction; label: string; needsCategory?: boolean }[] = [
  { action: 'later', label: 'Later (30 days)' },
  { action: 'never', label: 'Not this one' },
  { action: 'never_kind', label: 'Not this kind', needsCategory: true },
  { action: 'done', label: 'I reviewed it' },
];

/** A review's line: stars, the place's name and when */
const ReviewRow: React.FC<{ name: string; rating: number | null; when: string }> = ({
  name,
  rating,
  when,
}) => (
  <Box sx={{ minWidth: 0 }}>
    <Box sx={{ display: 'flex', gap: 0.75, alignItems: 'center', minWidth: 0 }}>
      <Stars rating={rating} />
      <Typography variant="body2" dir="auto" noWrap>
        {name}
      </Typography>
    </Box>
    <Typography variant="caption" sx={{ color: tokens.textMuted }}>
      {when}
    </Typography>
  </Box>
);

/**
 * The Reviews section.
 * @param props - Component props
 * @returns The section, or nothing before any review or reminder
 */
const ReviewsSection: React.FC<ReviewsSectionProps> = ({ onShowPlace }) => {
  const [data, setData] = useState<ReviewsOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [menu, setMenu] = useState<{ anchor: HTMLElement; due: ReviewDue } | null>(null);

  const load = () =>
    backendApiClient
      .atlasReviews()
      .then(setData)
      .catch((e: Error) => setError(e.message));

  useEffect(() => {
    let alive = true;
    backendApiClient
      .atlasReviews()
      .then((d) => alive && setData(d))
      .catch((e: Error) => alive && setError(e.message));
    return () => {
      alive = false;
    };
  }, []);

  const choose = async (due: ReviewDue, action: ReviewAction) => {
    setMenu(null);
    setError(null);
    try {
      await backendApiClient.atlasReviewReminder(due.placeId, action);
      // A whole kind may go at once: the list comes again
      await load();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  if (!data || (data.written === 0 && data.dueTotal === 0)) {
    return error ? (
      <Typography variant="caption" color="error" sx={{ px: 1.5 }}>
        {error}
      </Typography>
    ) : null;
  }

  return (
    <Box component="section" aria-label="Reviews" sx={{ px: 1.5, pt: 1.5 }}>
      <Typography variant="subtitle2" sx={{ color: tokens.textSecondary }}>
        Reviews
      </Typography>
      <Typography variant="caption" component="div" sx={{ color: tokens.textMuted, mb: 0.5 }}>
        {overviewText(data)}
      </Typography>

      {data.due.length > 0 && (
        <>
          <Typography variant="caption" component="div" sx={{ fontWeight: 600, mt: 0.5 }}>
            To review ({data.dueTotal})
          </Typography>
          {data.due.map((d) => (
            <Box key={d.placeId} sx={rowSx}>
              <ButtonBase
                onClick={() => onShowPlace(d.lat, d.lon)}
                sx={{ flex: 1, minWidth: 0, justifyContent: 'flex-start', textAlign: 'left' }}
              >
                <Box sx={{ minWidth: 0 }}>
                  <Typography variant="body2" dir="auto" noWrap>
                    {d.name}
                  </Typography>
                  <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                    {dueText(d.category, d.lastVisit, d.visits)}
                  </Typography>
                </Box>
              </ButtonBase>
              <IconButton
                size="small"
                href={d.writeUrl}
                target="_blank"
                rel="noopener noreferrer"
                aria-label={`Write a review of ${d.name}`}
              >
                <RateReview fontSize="small" />
              </IconButton>
              <IconButton
                size="small"
                aria-label={`Reminder choices for ${d.name}`}
                onClick={(e) => setMenu({ anchor: e.currentTarget, due: d })}
              >
                <MoreVert fontSize="small" />
              </IconButton>
            </Box>
          ))}
        </>
      )}

      {data.recent.length > 0 && (
        <>
          <Typography variant="caption" component="div" sx={{ fontWeight: 600, mt: 1 }}>
            Latest reviews
          </Typography>
          {data.recent.map((r) =>
            r.placeId ? (
              <ButtonBase key={r.id} onClick={() => onShowPlace(r.lat, r.lon)} sx={rowSx}>
                <ReviewRow name={r.name} rating={r.rating} when={reviewedText(r)} />
              </ButtonBase>
            ) : (
              <Box key={r.id} sx={rowSx}>
                {safeUrl(r.mapsUrl) ? (
                  <Link
                    href={safeUrl(r.mapsUrl)!}
                    target="_blank"
                    rel="noopener noreferrer"
                    underline="none"
                    color="inherit"
                    sx={{ minWidth: 0 }}
                  >
                    <ReviewRow name={r.name} rating={r.rating} when={reviewedText(r)} />
                  </Link>
                ) : (
                  <ReviewRow name={r.name} rating={r.rating} when={reviewedText(r)} />
                )}
              </Box>
            ),
          )}
        </>
      )}

      {error && (
        <Typography variant="caption" color="error" component="div">
          {error}
        </Typography>
      )}

      <Menu open={!!menu} anchorEl={menu?.anchor} onClose={() => setMenu(null)}>
        {CHOICES.filter((c) => !c.needsCategory || menu?.due.category).map((c) => (
          <MenuItem key={c.action} onClick={() => menu && void choose(menu.due, c.action)}>
            {c.label}
          </MenuItem>
        ))}
      </Menu>
    </Box>
  );
};

export default ReviewsSection;
