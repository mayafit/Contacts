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
  IconButton,
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
      const r = await backendApiClient.atlasCreateToken(name);
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
          Cloudflare Access, so remote clients also send a Cloudflare service token.
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
                {t.name} · <code>{t.prefix}…</code>
              </Typography>
              <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                Created {new Date(t.createdAt).toLocaleDateString('en-GB')}
                {t.lastUsedAt
                  ? ` · last used ${new Date(t.lastUsedAt).toLocaleString('en-GB')}`
                  : ' · not used yet'}
              </Typography>
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
