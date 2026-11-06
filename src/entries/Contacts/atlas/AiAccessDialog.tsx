/**
 * @fileoverview AI access: personal access tokens for MCP clients, and how to connect them
 * @module Contacts/atlas/AiAccessDialog
 */

import React, { useEffect, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  IconButton,
  MenuItem,
  Switch,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import { ContentCopy, DeleteOutline } from '@mui/icons-material';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import type { AccessTokenInfo } from './types';
import { tokens } from '../theme/contactsTheme';
import { fs } from '../settings/uiSettings';

/** Props for AiAccessDialog */
export interface AiAccessDialogProps {
  open: boolean;
  onClose: () => void;
}

/**
 * AI access dialog.
 * @param props - Component props
 * @returns Dialog
 */
const AiAccessDialog: React.FC<AiAccessDialogProps> = ({ open, onClose }) => {
  const [list, setList] = useState<AccessTokenInfo[]>([]);
  const [name, setName] = useState('Claude');
  const [allowWrite, setAllowWrite] = useState(false);
  const [days, setDays] = useState(90);
  const [created, setCreated] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const url = `${window.location.origin}/api/mcp`;

  const load = () =>
    backendApiClient
      .atlasTokens()
      .then(setList)
      .catch((e) => setError((e as Error).message));

  useEffect(() => {
    if (open) {
      setCreated(null);
      setError(null);
      void load();
    }
  }, [open]);

  const create = async () => {
    try {
      const r = await backendApiClient.atlasCreateToken(name, allowWrite, days);
      setCreated(r.token);
      void load();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const command = created
    ? `claude mcp add --transport http atlas ${url} --header "Authorization: Bearer ${created}" --header "CF-Access-Client-Id: <service token id>" --header "CF-Access-Client-Secret: <service token secret>"`
    : '';

  return (
    <Dialog open={open} onClose={onClose} maxWidth="md" fullWidth>
      <DialogTitle>AI access (MCP)</DialogTitle>
      <DialogContent>
        <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 2 }}>
          AI tools that speak MCP (Claude Code, Claude Desktop, …) can search and edit your Atlas
          data with a personal access token. The server is <code>{url}</code>. The site is behind
          Cloudflare Access, so remote clients also send a Cloudflare service token. A token can
          only read unless you allow changes, and it stops working when it expires.
        </Typography>
        {error && (
          <Alert severity="error" sx={{ mb: 2 }}>
            {error}
          </Alert>
        )}
        <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', mb: 2 }}>
          <TextField
            size="small"
            label="Name"
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
          <TextField
            select
            size="small"
            label="Expires after"
            value={days}
            onChange={(e) => setDays(Number(e.target.value))}
            sx={{ minWidth: 140 }}
          >
            {[30, 90, 180, 365].map((d) => (
              <MenuItem key={d} value={d}>
                {d} days
              </MenuItem>
            ))}
          </TextField>
          <FormControlLabel
            control={
              <Switch checked={allowWrite} onChange={(e) => setAllowWrite(e.target.checked)} />
            }
            label="Allow changes"
          />
          <Button variant="contained" onClick={create}>
            Create token
          </Button>
        </Box>
        {created && (
          <Alert severity="success" sx={{ mb: 2 }}>
            <Typography variant="body2" sx={{ mb: 1 }}>
              Copy it now: it is not shown again.
            </Typography>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
              <Box component="code" sx={{ wordBreak: 'break-all', fontSize: fs(14) }}>
                {created}
              </Box>
              <IconButton
                size="small"
                onClick={() => navigator.clipboard?.writeText(created)}
                aria-label="Copy token"
              >
                <ContentCopy fontSize="small" />
              </IconButton>
            </Box>
            <Typography variant="body2" sx={{ mt: 1.5, mb: 0.5 }}>
              Claude Code:
            </Typography>
            <Box sx={{ display: 'flex', alignItems: 'flex-start', gap: 1 }}>
              <Box component="code" sx={{ wordBreak: 'break-all', fontSize: fs(13) }}>
                {command}
              </Box>
              <IconButton
                size="small"
                onClick={() => navigator.clipboard?.writeText(command)}
                aria-label="Copy command"
              >
                <ContentCopy fontSize="small" />
              </IconButton>
            </Box>
          </Alert>
        )}
        {list.map((t) => (
          <Box
            key={t.id}
            sx={{
              display: 'flex',
              alignItems: 'center',
              gap: 1,
              py: 0.75,
              borderBottom: `1px solid ${tokens.border}`,
            }}
          >
            <Box sx={{ flex: 1 }}>
              <Typography variant="body2">
                {t.name} · <code>{t.prefix}…</code> · {t.canWrite ? 'can change data' : 'read only'}
              </Typography>
              <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                Created {new Date(t.createdAt).toLocaleDateString('en-GB')}
                {t.lastUsedAt
                  ? ` · last used ${new Date(t.lastUsedAt).toLocaleString('en-GB')}`
                  : ' · not used yet'}
                {new Date(t.expiresAt) <= new Date()
                  ? ' · expired'
                  : ` · expires ${new Date(t.expiresAt).toLocaleDateString('en-GB')}`}
              </Typography>
              {t.legacy && (
                <Typography variant="caption" component="div" sx={{ color: tokens.textSecondary }}>
                  Made before tokens expired: it keeps changing data until{' '}
                  {new Date(t.expiresAt).toLocaleDateString('en-GB')}. Create a new token to replace
                  it.
                </Typography>
              )}
            </Box>
            <Tooltip title="Revoke">
              <IconButton
                size="small"
                onClick={async () => {
                  await backendApiClient.atlasRevokeToken(t.id);
                  void load();
                }}
                aria-label="Revoke token"
              >
                <DeleteOutline fontSize="small" />
              </IconButton>
            </Tooltip>
          </Box>
        ))}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Close</Button>
      </DialogActions>
    </Dialog>
  );
};

export default AiAccessDialog;
