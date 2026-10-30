/**
 * @fileoverview The place panel's review block (AD-39): the Owner's Google Maps review of
 * the place, or the reminder to write one and the choices to set it aside
 * @module Contacts/atlas/PlaceReview
 */

import React, { useState } from 'react';
import { Box, Button, Link, Typography } from '@mui/material';
import { RateReview } from '@mui/icons-material';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import type { PlaceDetail, ReviewAction, ReviewState } from './types';
import PanelSection from './PanelSection';
import { reminderText, reviewedText, safeUrl, starsLabel } from './reviewText';
import { tokens } from '../theme/contactsTheme';

/** The colour of a filled star */
export const STAR_COLOR = '#f5a623';

/**
 * Five stars, the first `rating` filled.
 * @param props - The rating (1 to 5)
 * @returns Stars
 */
export const Stars: React.FC<{ rating: number | null | undefined }> = ({ rating }) =>
  rating ? (
    <Box
      component="span"
      role="img"
      aria-label={starsLabel(rating) ?? undefined}
      sx={{ letterSpacing: 1, whiteSpace: 'nowrap' }}
    >
      <Box component="span" sx={{ color: STAR_COLOR }}>
        {'★'.repeat(rating)}
      </Box>
      <Box component="span" sx={{ color: tokens.border }}>
        {'★'.repeat(5 - rating)}
      </Box>
    </Box>
  ) : null;

export interface PlaceReviewBlockProps {
  place: PlaceDetail;
  /** Called with the place's new state after a choice */
  onState: (state: ReviewState) => void;
}

/**
 * The Owner's review of the place, or the reminder to write one.
 * @param props - Component props
 * @returns The block, or nothing when the place is not one to review
 */
const PlaceReviewBlock: React.FC<PlaceReviewBlockProps> = ({ place, onState }) => {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState(false);
  const review = place.review;
  const state = place.reviewState;

  const choose = async (action: ReviewAction, kind = false) => {
    setBusy(true);
    setError(null);
    try {
      onState(await backendApiClient.atlasReviewReminder(place.id, action, kind));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  if (review) {
    const long = (review.text?.length ?? 0) > 280;
    return (
      <PanelSection
        title="Your review"
        hint={
          <Box component="span" aria-hidden>
            <Stars rating={review.rating} />
          </Box>
        }
      >
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, flexWrap: 'wrap' }}>
          <Stars rating={review.rating} />
          <Typography variant="caption" sx={{ color: tokens.textMuted }}>
            {reviewedText(review)}
          </Typography>
        </Box>
        {review.text && (
          <Typography
            variant="body2"
            dir="auto"
            sx={{
              mt: 0.5,
              whiteSpace: 'pre-wrap',
              ...(long && !expanded
                ? {
                    display: '-webkit-box',
                    WebkitLineClamp: 6,
                    WebkitBoxOrient: 'vertical',
                    overflow: 'hidden',
                  }
                : {}),
            }}
          >
            {review.text}
          </Typography>
        )}
        {long && (
          <Link component="button" variant="caption" onClick={() => setExpanded((x) => !x)}>
            {expanded ? 'less' : 'more'}
          </Link>
        )}
        {review.answers.map((a, i) => (
          <Typography
            key={`${i}-${a.question}`}
            variant="caption"
            component="div"
            dir="auto"
            sx={{ color: tokens.textSecondary }}
          >
            {a.question}: {a.answer}
          </Typography>
        ))}
        {safeUrl(review.mapsUrl) && (
          <Box sx={{ mt: 0.5 }}>
            <Link
              href={safeUrl(review.mapsUrl)!}
              target="_blank"
              rel="noopener noreferrer"
              underline="hover"
              variant="body2"
            >
              Open on Google Maps
            </Link>
            <Typography variant="caption" sx={{ color: tokens.textMuted, ml: 0.75 }}>
              with the photos you added
            </Typography>
          </Box>
        )}
      </PanelSection>
    );
  }

  const line = reminderText(state);
  if (!state || !line) {
    return null;
  }

  return (
    <PanelSection title="Review">
      <Typography variant="body2">{line}</Typography>
      {state.state === 'to_review' ? (
        <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.5, mt: 0.75 }}>
          {state.writeUrl && (
            <Button
              size="small"
              variant="outlined"
              startIcon={<RateReview />}
              href={state.writeUrl}
              target="_blank"
              rel="noopener noreferrer"
            >
              Write a review
            </Button>
          )}
          <Button size="small" disabled={busy} onClick={() => void choose('later')}>
            Later
          </Button>
          <Button size="small" disabled={busy} onClick={() => void choose('never')}>
            Not this one
          </Button>
          {place.category && (
            <Button size="small" disabled={busy} onClick={() => void choose('never_kind')}>
              Not this kind
            </Button>
          )}
          <Button size="small" disabled={busy} onClick={() => void choose('done')}>
            I reviewed it
          </Button>
        </Box>
      ) : (
        <Button
          size="small"
          disabled={busy}
          sx={{ mt: 0.5 }}
          onClick={() => void choose('clear', !!state.category)}
        >
          Remind me again
        </Button>
      )}
      {error && (
        <Typography variant="caption" color="error" component="div">
          {error}
        </Typography>
      )}
    </PanelSection>
  );
};

export default PlaceReviewBlock;
