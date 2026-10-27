/**
 * @fileoverview A photo at a large size, with when and where it was taken; arrows
 * (and the keyboard) go through the list it was opened from
 * @module Contacts/atlas/PhotoViewer
 */

import React, { useEffect } from 'react';
import { Box, Button, Dialog, IconButton, Typography } from '@mui/material';
import { ChevronLeft, ChevronRight, Close, OpenInNew, Place } from '@mui/icons-material';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import type { PhotoSummary } from './types';
import { isSet } from './types';
import { placedText, takenText } from './photos';
import { tokens } from '../theme/contactsTheme';

/** Props for PhotoViewer */
export interface PhotoViewerProps {
  photos: PhotoSummary[];
  /** The photo shown; null: closed */
  index: number | null;
  onIndex: (index: number | null) => void;
  /** Marks the photo's position on the map */
  onShowOnMap?: (lat: number, lon: number) => void;
}

/**
 * The viewer.
 * @param props - Component props
 * @returns Dialog
 */
const PhotoViewer: React.FC<PhotoViewerProps> = ({ photos, index, onIndex, onShowOnMap }) => {
  const photo = index !== null ? photos[index] : undefined;
  const go = (step: number) => {
    if (index !== null && photos.length > 0) {
      onIndex((index + step + photos.length) % photos.length);
    }
  };

  useEffect(() => {
    if (index === null) {
      return undefined;
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'ArrowRight') {
        go(1);
      } else if (e.key === 'ArrowLeft') {
        go(-1);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  const placed = photo ? placedText(photo) : null;
  return (
    <Dialog
      open={!!photo}
      onClose={() => onIndex(null)}
      maxWidth="lg"
      PaperProps={{ sx: { bgcolor: '#0b0d11', backgroundImage: 'none' } }}
    >
      {photo && (
        <Box sx={{ position: 'relative' }}>
          <Box
            sx={{
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              minWidth: { xs: 280, sm: 480 },
              minHeight: 320,
            }}
          >
            <img
              key={photo.id}
              src={backendApiClient.atlasPhotoUrl(photo.id, 'large')}
              alt={photo.name}
              style={{ maxWidth: '100%', maxHeight: '75vh', display: 'block' }}
            />
          </Box>
          <IconButton
            aria-label="Close"
            onClick={() => onIndex(null)}
            sx={{
              position: 'absolute',
              top: 8,
              right: 8,
              color: '#fff',
              bgcolor: 'rgba(0,0,0,0.4)',
            }}
          >
            <Close />
          </IconButton>
          {photos.length > 1 && (
            <>
              <IconButton
                aria-label="Previous photo"
                onClick={() => go(-1)}
                sx={{
                  position: 'absolute',
                  top: '40%',
                  left: 8,
                  color: '#fff',
                  bgcolor: 'rgba(0,0,0,0.4)',
                }}
              >
                <ChevronLeft />
              </IconButton>
              <IconButton
                aria-label="Next photo"
                onClick={() => go(1)}
                sx={{
                  position: 'absolute',
                  top: '40%',
                  right: 8,
                  color: '#fff',
                  bgcolor: 'rgba(0,0,0,0.4)',
                }}
              >
                <ChevronRight />
              </IconButton>
            </>
          )}
          <Box
            sx={{
              px: 2,
              py: 1.25,
              display: 'flex',
              alignItems: 'center',
              gap: 1.5,
              flexWrap: 'wrap',
            }}
          >
            <Box sx={{ flex: 1, minWidth: 200 }}>
              <Typography variant="body2" sx={{ color: '#fff' }}>
                {takenText(photo.takenAt)}
                {photos.length > 1 && (
                  <span style={{ color: tokens.textMuted }}>
                    {' '}
                    · {(index ?? 0) + 1} of {photos.length}
                  </span>
                )}
              </Typography>
              <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                {[photo.camera, placed].filter(Boolean).join(' · ') || photo.name}
              </Typography>
            </Box>
            {onShowOnMap && isSet(photo.lat) && isSet(photo.lon) && (
              <Button
                size="small"
                startIcon={<Place />}
                onClick={() => {
                  onShowOnMap(photo.lat!, photo.lon!);
                  onIndex(null);
                }}
              >
                On the map
              </Button>
            )}
            {photo.webUrl && (
              <Button
                size="small"
                startIcon={<OpenInNew />}
                href={photo.webUrl}
                target="_blank"
                rel="noopener noreferrer"
              >
                Open in OneDrive
              </Button>
            )}
          </Box>
        </Box>
      )}
    </Dialog>
  );
};

export default PhotoViewer;
