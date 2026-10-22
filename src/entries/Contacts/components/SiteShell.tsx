/**
 * @fileoverview The personal site's frame: an area rail (bottom bar on phones)
 * and the areas, Contacts and Atlas
 * @module Contacts/components/SiteShell
 */

import React, { useEffect } from 'react';
import { useSelector } from 'react-redux';
import { Navigate, Route, Routes, useLocation, useNavigate } from 'react-router';
import { Box, ButtonBase, Tooltip, Typography } from '@mui/material';
import { ContactPhone, Map as MapIcon } from '@mui/icons-material';
import { selectIsAuthenticated } from '../redux/slices/auth/selectors';
import SignedOutHome from './SignedOutHome';
import { ContactsWorkspace } from './ContactsHome';
import AtlasHome from '../atlas/AtlasHome';
import { tokens } from '../theme/contactsTheme';

const AREAS = [
  { path: '/contacts', label: 'Contacts', icon: <ContactPhone /> },
  { path: '/atlas', label: 'Atlas', icon: <MapIcon /> },
];

const LAST_AREA = 'site.lastArea';

/** Opens the area used last (Contacts the first time) */
const LastArea: React.FC = () => {
  let last = '/contacts';
  try {
    last = localStorage.getItem(LAST_AREA) ?? last;
  } catch {
    // Not stored: Contacts
  }
  const { search } = useLocation();
  return (
    <Navigate to={`${AREAS.some((a) => a.path === last) ? last : '/contacts'}${search}`} replace />
  );
};

/**
 * Site frame for a signed-in user; the welcome page otherwise.
 * @returns Frame element
 */
const SiteShell: React.FC = () => {
  const isAuthenticated = useSelector(selectIsAuthenticated);
  const location = useLocation();
  const navigate = useNavigate();
  const current = AREAS.find((a) => location.pathname.startsWith(a.path));

  useEffect(() => {
    if (current) {
      try {
        localStorage.setItem(LAST_AREA, current.path);
      } catch {
        // Not remembered
      }
    }
  }, [current]);

  if (!isAuthenticated) {
    return <SignedOutHome />;
  }

  return (
    <Box
      sx={{
        height: '100dvh',
        display: 'flex',
        flexDirection: { xs: 'column-reverse', sm: 'row' },
        bgcolor: tokens.page,
      }}
    >
      <Box
        component="nav"
        aria-label="Areas"
        sx={{
          display: 'flex',
          flexDirection: { xs: 'row', sm: 'column' },
          alignItems: 'center',
          justifyContent: { xs: 'space-around', sm: 'flex-start' },
          gap: { sm: 0.5 },
          width: { xs: '100%', sm: 64 },
          height: { xs: 56, sm: '100%' },
          pt: { sm: 1.5 },
          flexShrink: 0,
          bgcolor: tokens.surface,
          borderRight: { sm: `1px solid ${tokens.border}` },
          borderTop: { xs: `1px solid ${tokens.border}`, sm: 'none' },
        }}
      >
        {AREAS.map((a) => {
          const active = current?.path === a.path;
          return (
            <Tooltip key={a.path} title={a.label} placement="right">
              <ButtonBase
                onClick={() => navigate(a.path)}
                aria-current={active ? 'page' : undefined}
                sx={{
                  flexDirection: 'column',
                  gap: 0.25,
                  width: 56,
                  py: 0.75,
                  borderRadius: '10px',
                  color: active ? tokens.accent : tokens.textSecondary,
                  bgcolor: active ? 'rgba(57,135,229,0.14)' : 'transparent',
                  '&:hover': { bgcolor: tokens.hover },
                }}
              >
                {a.icon}
                <Typography sx={{ fontSize: 10.5, fontWeight: 600 }}>{a.label}</Typography>
              </ButtonBase>
            </Tooltip>
          );
        })}
      </Box>
      <Box sx={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        <Routes>
          <Route path="/contacts" element={<ContactsWorkspace />} />
          <Route path="/atlas" element={<AtlasHome />} />
          <Route path="*" element={<LastArea />} />
        </Routes>
      </Box>
    </Box>
  );
};

export default SiteShell;
