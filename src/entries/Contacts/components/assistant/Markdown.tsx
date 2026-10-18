/**
 * @fileoverview Renders the assistant's markdown safely (React nodes only, no HTML)
 * @module Contacts/components/assistant/Markdown
 */

import React from 'react';
import { Box, Link, Typography } from '@mui/material';
import { tokens } from '../../theme/contactsTheme';

/** Props for Markdown */
export interface MarkdownProps {
  /** Markdown text */
  text: string;
  /** Open a contact referenced as [Name](contact:people/...) */
  onContact: (resourceName: string) => void;
}

const INLINE = /(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\([^)\s]+\))/g;

/**
 * Inline markdown: **bold**, `code`, [text](link) with contact: and https: links.
 * @param text - Line text
 * @param onContact - Contact link handler
 * @returns Nodes
 */
const inline = (text: string, onContact: (rn: string) => void): React.ReactNode[] =>
  text.split(INLINE).map((part, i) => {
    if (part.startsWith('**') && part.endsWith('**') && part.length > 4) {
      return <strong key={i}>{part.slice(2, -2)}</strong>;
    }
    if (part.startsWith('`') && part.endsWith('`') && part.length > 2) {
      return (
        <Box
          key={i}
          component="code"
          sx={{ fontSize: '0.9em', px: 0.5, borderRadius: '4px', bgcolor: tokens.page }}
        >
          {part.slice(1, -1)}
        </Box>
      );
    }
    const link = /^\[([^\]]+)\]\(([^)\s]+)\)$/.exec(part);
    if (link) {
      const [, label, href] = link;
      if (href.startsWith('contact:')) {
        const rn = href.slice('contact:'.length);
        return (
          <Link
            key={i}
            component="button"
            type="button"
            underline="hover"
            onClick={() => onContact(rn)}
            sx={{ fontSize: 'inherit', verticalAlign: 'baseline', fontWeight: 600 }}
          >
            {label}
          </Link>
        );
      }
      if (/^https?:\/\//.test(href)) {
        return (
          <Link key={i} href={href} target="_blank" rel="noopener noreferrer" underline="hover">
            {label}
          </Link>
        );
      }
      return <React.Fragment key={i}>{label}</React.Fragment>;
    }
    return <React.Fragment key={i}>{part}</React.Fragment>;
  });

/**
 * Markdown blocks: paragraphs, headings, bullet and numbered lists, tables, code.
 * @param props - Component props
 * @returns Rendered text
 */
const Markdown: React.FC<MarkdownProps> = ({ text, onContact }) => {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  const blocks: React.ReactNode[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (line.trim().startsWith('```')) {
      const code: string[] = [];
      i += 1;
      while (i < lines.length && !lines[i].trim().startsWith('```')) {
        code.push(lines[i]);
        i += 1;
      }
      i += 1;
      blocks.push(
        <Box
          key={blocks.length}
          component="pre"
          sx={{
            fontSize: 12,
            p: 1,
            borderRadius: '6px',
            bgcolor: tokens.page,
            overflowX: 'auto',
            m: 0,
          }}
        >
          {code.join('\n')}
        </Box>,
      );
      continue;
    }
    if (line.trim().startsWith('|')) {
      const rows: string[][] = [];
      while (i < lines.length && lines[i].trim().startsWith('|')) {
        const cells = lines[i]
          .trim()
          .replace(/^\||\|$/g, '')
          .split('|')
          .map((c) => c.trim());
        if (!cells.every((c) => /^:?-{2,}:?$/.test(c))) {
          rows.push(cells);
        }
        i += 1;
      }
      blocks.push(
        <Box key={blocks.length} sx={{ overflowX: 'auto' }}>
          <Box component="table" sx={{ borderCollapse: 'collapse', fontSize: 13, width: '100%' }}>
            <tbody>
              {rows.map((r, ri) => (
                <tr key={ri}>
                  {r.map((c, ci) => (
                    <Box
                      key={ci}
                      component={ri === 0 ? 'th' : 'td'}
                      dir="auto"
                      sx={{
                        textAlign: 'start',
                        p: 0.5,
                        borderBottom: `1px solid ${tokens.border}`,
                        fontWeight: ri === 0 ? 600 : 400,
                      }}
                    >
                      {inline(c, onContact)}
                    </Box>
                  ))}
                </tr>
              ))}
            </tbody>
          </Box>
        </Box>,
      );
      continue;
    }
    const bullet = /^\s*(?:[-*•]|(\d+)[.)])\s+(.*)$/.exec(line);
    if (bullet) {
      const ordered = !!bullet[1];
      const items: string[] = [];
      while (i < lines.length) {
        const m = /^\s*(?:[-*•]|(\d+)[.)])\s+(.*)$/.exec(lines[i]);
        if (!m || !!m[1] !== ordered) {
          break;
        }
        items.push(m[2]);
        i += 1;
      }
      blocks.push(
        <Box
          key={blocks.length}
          component={ordered ? 'ol' : 'ul'}
          // Markers inside the line: a right-to-left item would put an outside marker past the edge
          sx={{ m: 0, pl: 0.5, listStylePosition: 'inside', '& li': { mb: 0.25 } }}
        >
          {items.map((t, ti) => (
            <li key={ti} dir="auto">
              {inline(t, onContact)}
            </li>
          ))}
        </Box>,
      );
      continue;
    }
    const heading = /^#{1,4}\s+(.*)$/.exec(line);
    if (heading) {
      blocks.push(
        <Typography key={blocks.length} variant="subtitle2" dir="auto" sx={{ mt: 0.5 }}>
          {inline(heading[1], onContact)}
        </Typography>,
      );
      i += 1;
      continue;
    }
    if (line.trim()) {
      const para: string[] = [];
      while (
        i < lines.length &&
        lines[i].trim() &&
        !/^\s*(?:[-*•]|\d+[.)])\s+/.test(lines[i]) &&
        !/^#{1,4}\s/.test(lines[i]) &&
        !lines[i].trim().startsWith('|') &&
        !lines[i].trim().startsWith('```')
      ) {
        para.push(lines[i]);
        i += 1;
      }
      blocks.push(
        <Typography key={blocks.length} variant="body2" dir="auto" component="p" sx={{ m: 0 }}>
          {para.map((p, pi) => (
            <React.Fragment key={pi}>
              {pi > 0 && <br />}
              {inline(p, onContact)}
            </React.Fragment>
          ))}
        </Typography>,
      );
      continue;
    }
    i += 1;
  }
  return <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}>{blocks}</Box>;
};

export default Markdown;
