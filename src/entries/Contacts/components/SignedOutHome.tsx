/**
 * @fileoverview What a signed-out visitor sees: the app's header with a sign-in
 * button (where Sign out is when signed in) and what the app does
 * @module Contacts/components/SignedOutHome
 */

import React from 'react';
import { useLocation } from 'react-router';
import { Alert, Box, Button, Paper, Typography } from '@mui/material';
import {
  AutoAwesome,
  CleaningServices,
  ContactPhone,
  Map as MapIcon,
  Insights as InsightsIcon,
  Login as LoginIcon,
  RecordVoiceOver,
} from '@mui/icons-material';
import { GoogleContactsService } from '../services/GoogleContactsService';
import { logger } from '../../../shared/logger';
import { tokens } from '../theme/contactsTheme';
import { fs } from '../settings/uiSettings';

/** Messages for the ?error= codes the backend and the session watcher send */
export const SIGN_IN_ERRORS: Record<string, string> = {
  invalid_state: 'Sign-in failed (the request expired). Please try again.',
  no_code: 'Google did not complete the sign-in. Please try again.',
  config_error: 'The server is not configured for Google sign-in.',
  auth_failed: 'Sign-in failed. Please try again.',
  access_denied: 'Sign-in was cancelled.',
  session_expired:
    'Your Google session ended. Sign in again; you will not need to approve access again.',
  missing_scope:
    'Google did not grant access to your contacts. Sign in again and tick "See, edit, download, and permanently delete your contacts".',
};

const FEATURES = [
  {
    icon: <MapIcon />,
    title: 'Atlas',
    text: 'Your trips, rides, hikes and heart rate on one map and timeline: Google Timeline, Sports Tracker, Google Fit and Fitbit together.',
  },
  {
    icon: <RecordVoiceOver />,
    title: 'Phonetic names',
    text: 'See and fill in how every name is pronounced, in a fast table you edit in place.',
  },
  {
    icon: <CleaningServices />,
    title: 'Clean up',
    text: 'Find duplicates, merge them (Hebrew and English versions too), and move what you do not need to Trash.',
  },
  {
    icon: <InsightsIcon />,
    title: 'Keeping in touch',
    text: 'Optionally connect Gmail and Calendar to see who you are in touch with, and who has gone quiet.',
  },
  {
    icon: <AutoAwesome />,
    title: 'Assistant',
    text: 'Ask about your contacts and have changes proposed; nothing is saved until you apply it.',
  },
];

/**
 * Signed-out home.
 * @returns Page element
 */
const SignedOutHome: React.FC = () => {
  const location = useLocation();
  const code = new URLSearchParams(location.search).get('error');
  const error = code ? (SIGN_IN_ERRORS[code] ?? 'Sign-in failed. Please try again.') : null;

  const signIn = () => {
    logger.info({ context: 'SignedOutHome' }, 'User initiated sign in');
    GoogleContactsService.login();
  };

  return (
    <Box
      sx={{
        minHeight: '100vh',
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
          gap: 2,
          px: { xs: 1.5, md: 2.5 },
          height: 60,
          flexShrink: 0,
          borderBottom: `1px solid ${tokens.border}`,
          bgcolor: tokens.surface,
        }}
      >
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.25 }}>
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
          <Typography variant="h3">Personal</Typography>
        </Box>
        <Box sx={{ flex: 1 }} />
        <Button variant="contained" startIcon={<LoginIcon />} onClick={signIn}>
          Sign in with Google
        </Button>
      </Box>

      <Box
        component="main"
        sx={{ flex: 1, display: 'grid', placeItems: 'center', p: { xs: 2, md: 4 } }}
      >
        <Box sx={{ maxWidth: 860, width: '100%' }}>
          {error && (
            <Alert severity={code === 'access_denied' ? 'info' : 'error'} sx={{ mb: 3 }}>
              {error}
            </Alert>
          )}
          <Typography
            sx={{ fontSize: { xs: 30, md: 40 }, fontWeight: 600, lineHeight: 1.15, mb: 1 }}
          >
            Your contacts and your places, organized.
          </Typography>
          <Typography variant="body1" sx={{ color: tokens.textSecondary, mb: 3 }}>
            Sign in with your Google account. Each account sees only its own contacts, trips and
            health data.
          </Typography>
          <Box
            sx={{
              display: 'grid',
              gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' },
              gap: 2,
              mb: 3,
            }}
          >
            {FEATURES.map((f) => (
              <Paper
                key={f.title}
                variant="outlined"
                sx={{ p: 2.5, borderColor: tokens.border, bgcolor: tokens.surface }}
              >
                <Box
                  sx={{
                    display: 'flex',
                    gap: 1,
                    alignItems: 'center',
                    color: tokens.accent,
                    mb: 0.75,
                  }}
                >
                  {f.icon}
                  <Typography variant="subtitle1" sx={{ color: tokens.text }}>
                    {f.title}
                  </Typography>
                </Box>
                <Typography variant="body2" sx={{ color: tokens.textMuted }}>
                  {f.text}
                </Typography>
              </Paper>
            ))}
          </Box>
          <Button variant="contained" size="large" startIcon={<LoginIcon />} onClick={signIn}>
            Sign in with Google
          </Button>
        </Box>
      </Box>
    </Box>
  );
};

export default SignedOutHome;
