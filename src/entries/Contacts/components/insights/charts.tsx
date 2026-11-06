/**
 * @fileoverview Small single-series charts for Insights: a horizontal bar list
 * and a column chart. One hue (the accent), recessive track, values as text at
 * the bar end, hover tooltip with exact figures. No legend: each chart is one
 * series named by its card title.
 * @module Contacts/components/insights/charts
 */

import React from 'react';
import { Box, ButtonBase, Tooltip, Typography } from '@mui/material';
import { ChevronRight } from '@mui/icons-material';
import { tokens } from '../../theme/contactsTheme';
import { fs } from '../../settings/uiSettings';

/** A bar */
export interface BarDatum {
  /** Row label */
  label: string;
  /** Value */
  value: number;
  /** Optional click action (e.g. open a filtered list) */
  onClick?: () => void;
  /** Tooltip text for the click action */
  actionHint?: string;
}

const fmt = new Intl.NumberFormat('en-US');

/**
 * Percent text with no false precision.
 * @param part - Part
 * @param whole - Whole
 * @returns e.g. "42%", "<1%", "0%"
 */
export const percent = (part: number, whole: number): string => {
  if (!whole || !part) {
    return '0%';
  }
  const p = (part / whole) * 100;
  return p < 1 ? '<1%' : `${Math.round(p)}%`;
};

/**
 * Horizontal bar list.
 * @param props - Data, scale maximum and value formatting
 * @returns Chart element
 */
export const BarList: React.FC<{
  data: BarDatum[];
  /** Value that fills the track (defaults to the largest value) */
  max?: number;
  /** Denominator for the percentage shown next to each value */
  total?: number;
  /** Accessible name */
  ariaLabel: string;
}> = ({ data, max, total, ariaLabel }) => {
  const scale = max ?? Math.max(1, ...data.map((d) => d.value));
  return (
    <Box
      role="list"
      aria-label={ariaLabel}
      sx={{ display: 'flex', flexDirection: 'column', gap: 0.25 }}
    >
      {data.map((d) => {
        const width = scale ? Math.max(d.value > 0 ? 1.5 : 0, (d.value / scale) * 100) : 0;
        const valueText = total
          ? `${fmt.format(d.value)} · ${percent(d.value, total)}`
          : fmt.format(d.value);
        const row = (
          <Box
            sx={{
              display: 'grid',
              gridTemplateColumns: '120px 1fr auto 18px',
              alignItems: 'center',
              gap: 1.5,
              width: '100%',
              py: 0.75,
              px: 1,
              borderRadius: '8px',
            }}
          >
            <Typography
              variant="body2"
              noWrap
              dir="auto"
              sx={{ color: tokens.textSecondary, textAlign: 'left' }}
            >
              {d.label}
            </Typography>
            <Box
              sx={{ height: 14, borderRadius: '4px', bgcolor: tokens.track, overflow: 'hidden' }}
            >
              <Box
                sx={{
                  height: '100%',
                  width: `${width}%`,
                  bgcolor: tokens.accent,
                  borderRadius: '0 4px 4px 0',
                  transition: 'width 300ms ease',
                }}
              />
            </Box>
            <Typography
              variant="body2"
              sx={{
                color: tokens.text,
                fontVariantNumeric: 'tabular-nums',
                minWidth: 84,
                textAlign: 'right',
              }}
            >
              {valueText}
            </Typography>
            {d.onClick ? (
              <ChevronRight sx={{ fontSize: fs.chart(18), color: tokens.textMuted }} />
            ) : (
              <span />
            )}
          </Box>
        );
        const tip = `${d.label}: ${fmt.format(d.value)}${total ? ` of ${fmt.format(total)} (${percent(d.value, total)})` : ''}${d.actionHint ? ` — ${d.actionHint}` : ''}`;
        return (
          <Tooltip key={d.label} title={tip} placement="top" disableInteractive>
            {d.onClick ? (
              <ButtonBase
                role="listitem"
                onClick={d.onClick}
                sx={{ width: '100%', borderRadius: '8px', '&:hover': { bgcolor: tokens.hover } }}
              >
                {row}
              </ButtonBase>
            ) : (
              <Box
                role="listitem"
                sx={{ '&:hover': { bgcolor: tokens.hover }, borderRadius: '8px' }}
              >
                {row}
              </Box>
            )}
          </Tooltip>
        );
      })}
    </Box>
  );
};

/**
 * Vertical column chart for an ordered series (e.g. months).
 * @param props - Data and accessible name
 * @returns Chart element
 */
export const ColumnChart: React.FC<{ data: BarDatum[]; ariaLabel: string; height?: number }> = ({
  data,
  ariaLabel,
  height = 150,
}) => {
  const max = Math.max(1, ...data.map((d) => d.value));
  return (
    <Box role="list" aria-label={ariaLabel}>
      <Box
        sx={{
          display: 'grid',
          gridTemplateColumns: `repeat(${data.length}, 1fr)`,
          alignItems: 'end',
          height,
          gap: '2px',
          borderBottom: `1px solid ${tokens.borderStrong}`,
        }}
      >
        {data.map((d) => (
          <Tooltip
            key={d.label}
            title={`${d.label}: ${fmt.format(d.value)}`}
            placement="top"
            disableInteractive
          >
            <Box
              role="listitem"
              aria-label={`${d.label}: ${d.value}`}
              sx={{
                height: '100%',
                display: 'flex',
                flexDirection: 'column',
                justifyContent: 'flex-end',
                alignItems: 'center',
                borderRadius: '6px 6px 0 0',
                '&:hover': { bgcolor: tokens.hover },
              }}
            >
              {d.value > 0 && (
                <Typography
                  variant="caption"
                  sx={{ color: tokens.textSecondary, mb: 0.5, fontVariantNumeric: 'tabular-nums' }}
                >
                  {fmt.format(d.value)}
                </Typography>
              )}
              <Box
                sx={{
                  width: '100%',
                  maxWidth: 24,
                  height: `${(d.value / max) * (height - 24)}px`,
                  minHeight: d.value > 0 ? 3 : 0,
                  bgcolor: tokens.accent,
                  borderRadius: '4px 4px 0 0',
                }}
              />
            </Box>
          </Tooltip>
        ))}
      </Box>
      <Box sx={{ display: 'grid', gridTemplateColumns: `repeat(${data.length}, 1fr)`, mt: 0.75 }}>
        {data.map((d) => (
          <Typography
            key={d.label}
            variant="caption"
            sx={{ color: tokens.textMuted, textAlign: 'center' }}
          >
            {d.label}
          </Typography>
        ))}
      </Box>
    </Box>
  );
};
