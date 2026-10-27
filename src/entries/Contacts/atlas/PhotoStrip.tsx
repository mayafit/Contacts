/**
 * @fileoverview A grid of photo thumbnails (an activity's or a place's photos)
 * @module Contacts/atlas/PhotoStrip
 */

import React, { useState } from 'react';
import { Box, ButtonBase, Link, Typography } from '@mui/material';
import { PlayCircleOutline } from '@mui/icons-material';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import type { PhotoSummary } from './types';
import { takenText } from './photos';
import { tokens } from '../theme/contactsTheme';

/** Props for PhotoStrip */
export interface PhotoStripProps {
  photos: PhotoSummary[];
  /** Opens the viewer at a photo */
  onOpen: (index: number) => void;
}

/** Shown before "Show all" */
const FIRST = 12;

/**
 * Thumbnails, three a row.
 * @param props - Component props
 * @returns The grid
 */
const PhotoStrip: React.FC<PhotoStripProps> = ({ photos, onOpen }) => {
  const [all, setAll] = useState(false);
  const shown = all ? photos : photos.slice(0, FIRST);
  return (
    <Box>
      <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 0.5 }}>
        {shown.map((p, i) => (
          <ButtonBase
            key={p.id}
            onClick={() => onOpen(i)}
            aria-label={`Open photo ${takenText(p.takenAt)}`}
            sx={{
              position: 'relative',
              aspectRatio: '1',
              borderRadius: '6px',
              overflow: 'hidden',
              bgcolor: tokens.page,
            }}
          >
            <img
              src={backendApiClient.atlasPhotoUrl(p.id, 'medium')}
              alt={p.name}
              loading="lazy"
              style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }}
            />
            {p.isVideo && (
              <PlayCircleOutline
                sx={{ position: 'absolute', right: 4, bottom: 4, fontSize: 20, color: '#fff' }}
              />
            )}
          </ButtonBase>
        ))}
      </Box>
      {photos.length > FIRST && (
        <Typography variant="caption" component="div" sx={{ mt: 0.5 }}>
          <Link component="button" type="button" onClick={() => setAll((v) => !v)}>
            {all ? 'Show fewer' : `Show all ${photos.length}`}
          </Link>
        </Typography>
      )}
    </Box>
  );
};

export default PhotoStrip;
