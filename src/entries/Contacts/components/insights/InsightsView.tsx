/**
 * @fileoverview Insights: what is in the address book and what needs fixing.
 * @module Contacts/components/insights/InsightsView
 */

import React, { useState } from 'react';
import { Box, Button, Chip, LinearProgress, Paper, Typography } from '@mui/material';
import { ArrowForward } from '@mui/icons-material';
import type { ContactFilter, ContactStats } from '../../model/stats';
import type { DomainInsight } from '../../model/organizations';
import { BarList, ColumnChart, percent } from './charts';
import { tokens } from '../../theme/contactsTheme';

/** Props for InsightsView */
export interface InsightsViewProps {
  /** Computed statistics */
  stats: ContactStats;
  /** Open the Contacts view with a filter */
  onShowFilter: (filter: ContactFilter) => void;
  /** Open the Duplicates view */
  onShowDuplicates: () => void;
  /** Work email domains and their organization suggestions */
  orgInsights: DomainInsight[];
  /** Review the suggestions for one domain */
  onReviewDomain: (insight: DomainInsight) => void;
  /** Keeping-in-touch section, shown under the headline tiles */
  activitySection?: React.ReactNode;
}

const fmt = new Intl.NumberFormat('en-US');

/**
 * Card with a title.
 * @param props - Title, optional subtitle and content
 * @returns Card element
 */
const Card: React.FC<{
  title: string;
  subtitle?: string;
  children: React.ReactNode;
  sx?: object;
}> = ({ title, subtitle, children, sx }) => (
  <Paper
    variant="outlined"
    sx={{ p: 2.5, borderColor: tokens.border, bgcolor: tokens.surface, ...sx }}
  >
    <Typography variant="subtitle1">{title}</Typography>
    {subtitle && (
      <Typography variant="caption" sx={{ color: tokens.textMuted }}>
        {subtitle}
      </Typography>
    )}
    <Box sx={{ mt: 1.5 }}>{children}</Box>
  </Paper>
);

/**
 * Stat tile: one number, a short explanation and an action.
 * @param props - Tile content
 * @returns Tile element
 */
const Tile: React.FC<{
  label: string;
  value: string;
  detail: string;
  progress?: number;
  action?: { label: string; onClick: () => void };
  attention?: boolean;
}> = ({ label, value, detail, progress, action, attention }) => (
  <Paper
    variant="outlined"
    sx={{
      p: 2.5,
      borderColor: tokens.border,
      bgcolor: tokens.surface,
      display: 'flex',
      flexDirection: 'column',
    }}
  >
    <Typography variant="subtitle2" sx={{ color: tokens.textSecondary }}>
      {label}
    </Typography>
    <Typography
      sx={{
        fontSize: 32,
        fontWeight: 600,
        lineHeight: 1.2,
        mt: 0.75,
        color: attention ? tokens.attention : tokens.text,
      }}
    >
      {value}
    </Typography>
    <Typography variant="body2" sx={{ color: tokens.textMuted, mt: 0.5, flex: 1 }}>
      {detail}
    </Typography>
    {progress !== undefined && (
      <LinearProgress
        variant="determinate"
        value={progress}
        aria-label={label}
        sx={{
          mt: 1.5,
          height: 6,
          borderRadius: 3,
          bgcolor: tokens.track,
          '& .MuiLinearProgress-bar': { borderRadius: 3 },
        }}
      />
    )}
    {action && (
      <Button
        size="small"
        endIcon={<ArrowForward />}
        onClick={action.onClick}
        sx={{ mt: 1.5, alignSelf: 'flex-start', ml: -1 }}
      >
        {action.label}
      </Button>
    )}
  </Paper>
);

/**
 * Insights view.
 * @param props - Component props
 * @returns View element
 */
const InsightsView: React.FC<InsightsViewProps> = ({
  stats,
  onShowFilter,
  onShowDuplicates,
  orgInsights,
  onReviewDomain,
  activitySection,
}) => {
  const [allDomains, setAllDomains] = useState(false);
  const toFix = orgInsights.reduce((s, d) => s + d.needsUpdate.length, 0);
  const shownDomains = allDomains ? orgInsights : orgInsights.slice(0, 8);
  const missingPhonetic = stats.total - stats.withPhonetic;
  const hebrewMissing = stats.hebrewNamed - stats.hebrewNamedWithPhonetic;
  const phoneRow = stats.completeness.find((r) => r.key === 'phone');
  const emailRow = stats.completeness.find((r) => r.key === 'email');

  return (
    <Box sx={{ p: { xs: 2, md: 3 }, overflowY: 'auto', flex: 1 }}>
      <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 2, mb: 3, flexWrap: 'wrap' }}>
        <Typography sx={{ fontSize: 56, fontWeight: 600, lineHeight: 1, letterSpacing: '-0.02em' }}>
          {fmt.format(stats.total)}
        </Typography>
        <Typography variant="body1" sx={{ color: tokens.textSecondary }}>
          contacts · {fmt.format(stats.phoneCount)} phone numbers
        </Typography>
      </Box>

      <Box
        sx={{
          display: 'grid',
          gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr', lg: 'repeat(4, 1fr)' },
          gap: 2,
          mb: 2,
        }}
      >
        <Tile
          label="Phonetic names"
          value={percent(stats.withPhonetic, stats.total)}
          detail={
            stats.hebrewNamed
              ? `${fmt.format(hebrewMissing)} of ${fmt.format(stats.hebrewNamed)} Hebrew names have no phonetic name`
              : `${fmt.format(missingPhonetic)} contacts have no phonetic name`
          }
          progress={stats.total ? (stats.withPhonetic / stats.total) * 100 : 0}
          attention={missingPhonetic > 0}
          action={
            missingPhonetic > 0
              ? { label: 'Fill in missing', onClick: () => onShowFilter('missingPhonetic') }
              : undefined
          }
        />
        <Tile
          label="Duplicates"
          value={fmt.format(stats.duplicateGroups)}
          detail={
            stats.duplicateGroups
              ? `groups covering ${fmt.format(stats.contactsInDuplicates)} contacts`
              : 'No likely duplicates found'
          }
          attention={stats.duplicateGroups > 0}
          action={
            stats.duplicateGroups ? { label: 'Review', onClick: onShowDuplicates } : undefined
          }
        />
        <Tile
          label="No phone"
          value={fmt.format(stats.total - (phoneRow?.count ?? 0))}
          detail={`${percent(phoneRow?.count ?? 0, stats.total)} of contacts have a phone`}
          action={{ label: 'Show', onClick: () => onShowFilter('noPhone') }}
        />
        <Tile
          label="No email"
          value={fmt.format(stats.total - (emailRow?.count ?? 0))}
          detail={`${percent(emailRow?.count ?? 0, stats.total)} of contacts have an email`}
          action={{ label: 'Show', onClick: () => onShowFilter('noEmail') }}
        />
      </Box>

      {activitySection}

      {orgInsights.length > 0 && (
        <Card
          title="Organizations from work email"
          subtitle={
            toFix
              ? `${fmt.format(toFix)} contacts with a work address have a missing or different organization`
              : 'Every contact with a work address lists the matching organization'
          }
          sx={{ mb: 2 }}
        >
          <Box role="table" aria-label="Work email domains">
            {shownDomains.map((d) => (
              <Box
                key={d.domain}
                role="row"
                sx={{
                  display: 'grid',
                  gridTemplateColumns: {
                    xs: '1fr auto',
                    md: 'minmax(160px, 1.2fr) minmax(140px, 1fr) 110px 150px auto',
                  },
                  alignItems: 'center',
                  gap: 1.5,
                  py: 0.75,
                  px: 1,
                  borderRadius: '8px',
                  '&:hover': { bgcolor: tokens.hover },
                }}
              >
                <Typography variant="body2" sx={{ fontWeight: 600 }}>
                  @{d.domain}
                </Typography>
                <Typography
                  variant="body2"
                  dir="auto"
                  sx={{
                    color: tokens.textSecondary,
                    display: { xs: 'none', md: 'block' },
                    textAlign: 'left',
                  }}
                >
                  {d.suggested}
                </Typography>
                <Typography
                  variant="body2"
                  sx={{
                    color: tokens.textMuted,
                    display: { xs: 'none', md: 'block' },
                    fontVariantNumeric: 'tabular-nums',
                  }}
                >
                  {fmt.format(d.members.length)} contacts
                </Typography>
                <Box sx={{ display: { xs: 'none', md: 'block' } }}>
                  {d.needsUpdate.length > 0 ? (
                    <Chip
                      size="small"
                      label={`${d.needsUpdate.length} to update`}
                      sx={{ bgcolor: 'rgba(232,162,58,0.15)', color: tokens.attention }}
                    />
                  ) : (
                    <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                      up to date
                    </Typography>
                  )}
                </Box>
                <Button
                  size="small"
                  onClick={() => onReviewDomain(d)}
                  disabled={d.needsUpdate.length === 0}
                >
                  Review
                </Button>
              </Box>
            ))}
          </Box>
          {orgInsights.length > 8 && (
            <Button size="small" sx={{ mt: 1 }} onClick={() => setAllDomains((v) => !v)}>
              {allDomains ? 'Show fewer' : `Show all ${orgInsights.length} domains`}
            </Button>
          )}
        </Card>
      )}

      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', lg: '1fr 1fr' }, gap: 2 }}>
        <Card
          title="Field completeness"
          subtitle="Contacts that have each field. Click a row to list the ones missing it."
        >
          <BarList
            ariaLabel="Field completeness"
            total={stats.total}
            max={stats.total}
            data={stats.completeness.map((r) => ({
              label: r.label,
              value: r.count,
              onClick: r.missingFilter ? () => onShowFilter(r.missingFilter!) : undefined,
              actionHint: r.missingFilter
                ? `click to list ${fmt.format(stats.total - r.count)} missing`
                : undefined,
            }))}
          />
        </Card>

        <Box sx={{ display: 'grid', gap: 2 }}>
          <Card title="Phone numbers by origin" subtitle="Every number, classified by its format">
            <BarList
              ariaLabel="Phone numbers by origin"
              total={stats.phoneCount}
              data={stats.phoneOrigins.map((r) => ({ label: r.label, value: r.count }))}
            />
          </Card>
          <Card title="Names by script">
            <BarList
              ariaLabel="Names by script"
              total={stats.total}
              data={stats.nameScripts
                .filter((r) => r.count > 0)
                .map((r) => ({ label: r.label, value: r.count }))}
            />
          </Card>
        </Box>

        <Card title="Top labels" subtitle={stats.topLabels.length ? undefined : 'No labels in use'}>
          {stats.topLabels.length > 0 && (
            <BarList
              ariaLabel="Top labels"
              data={stats.topLabels.map((r) => ({ label: r.label, value: r.count }))}
            />
          )}
        </Card>

        <Card
          title="Birthdays by month"
          subtitle={`${fmt.format(stats.birthdaysByMonth.reduce((s, m) => s + m.count, 0))} contacts have a birthday`}
        >
          <ColumnChart
            ariaLabel="Birthdays by month"
            data={stats.birthdaysByMonth.map((m) => ({ label: m.label, value: m.count }))}
          />
        </Card>
      </Box>
    </Box>
  );
};

export default InsightsView;
