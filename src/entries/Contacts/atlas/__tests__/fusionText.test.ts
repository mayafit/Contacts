import {
  describeEvidence,
  describeFusionStatus,
  formatDelta,
  formatMeters,
  formatPercent,
  formatSeconds,
  fusionBusy,
  locationToleranceError,
  minutesTextToSeconds,
  secondsToMinutesText,
  timeToleranceError,
} from '../fusionText';
import type { FusionStatus, MatchEvidence } from '../types';

const evidence = (patch: Partial<MatchEvidence> = {}): MatchEvidence => ({
  memberId: 'm2',
  source: 'google-timeline',
  startDeltaS: 40,
  endDeltaS: -120,
  timeToleranceS: 630,
  trackDeviationM: 18,
  locationToleranceM: 325,
  confidence: 0.94,
  userDecision: false,
  ...patch,
});

const status = (patch: Partial<FusionStatus> = {}): FusionStatus => ({
  pending: 0,
  running: false,
  lastRunAt: null,
  lastResult: null,
  ...patch,
});

describe('fusion settings input', () => {
  it('shows seconds as minutes', () => {
    expect(secondsToMinutesText(30)).toBe('0.5');
    expect(secondsToMinutesText(600)).toBe('10');
    expect(secondsToMinutesText(90)).toBe('1.5');
    expect(secondsToMinutesText(20)).toBe('0.333');
  });

  it('reads minutes back as whole seconds', () => {
    expect(minutesTextToSeconds('0.5')).toBe(30);
    expect(minutesTextToSeconds('10')).toBe(600);
    expect(minutesTextToSeconds(' 1,5 ')).toBe(90);
    expect(minutesTextToSeconds('.25')).toBe(15);
    expect(minutesTextToSeconds('1:30')).toBe(90);
    expect(minutesTextToSeconds('0:05')).toBe(5);
  });

  it('round-trips every second of a minute', () => {
    for (let s = 1; s <= 120; s++) {
      expect(minutesTextToSeconds(secondsToMinutesText(s))).toBe(s);
    }
  });

  it('rejects what is not a positive number', () => {
    expect(minutesTextToSeconds('')).toBeNull();
    expect(minutesTextToSeconds('abc')).toBeNull();
    expect(minutesTextToSeconds('-1')).toBeNull();
    expect(minutesTextToSeconds('0')).toBeNull();
    expect(minutesTextToSeconds('0:00')).toBeNull();
    expect(minutesTextToSeconds('1:75')).toBeNull();
  });

  it('checks the tolerance ranges', () => {
    expect(timeToleranceError('0.5')).toBeNull();
    expect(timeToleranceError('360')).toBeNull();
    expect(timeToleranceError('361')).toBe('1 s to 6 h');
    expect(timeToleranceError('x')).toBe('Minutes, e.g. 0.5 or 1:30');
    expect(locationToleranceError('25')).toBeNull();
    expect(locationToleranceError('20000')).toBeNull();
    expect(locationToleranceError('20001')).toBe('1 to 20,000 m');
    expect(locationToleranceError('0.5')).toBe('1 to 20,000 m');
    expect(locationToleranceError('')).toBe('Meters, e.g. 100');
  });
});

describe('fusion texts', () => {
  it('formats times, differences, distances and percentages', () => {
    expect(formatSeconds(40)).toBe('40 s');
    expect(formatSeconds(630)).toBe('10.5 min');
    expect(formatSeconds(5400)).toBe('1.5 h');
    expect(formatDelta(40)).toBe('+40 s');
    expect(formatDelta(-120)).toBe('−2 min');
    expect(formatDelta(0)).toBe('0 s');
    expect(formatMeters(18.4)).toBe('18 m');
    expect(formatMeters(1250)).toBe('1.3 km');
    expect(formatPercent(0.94)).toBe('94%');
  });

  it('describes match evidence', () => {
    expect(describeEvidence(evidence())).toBe(
      'Start +40 s · end −2 min (±10.5 min) · track 18 m apart (±325 m) · match 94%',
    );
  });

  it('says when there is no track to compare', () => {
    expect(describeEvidence(evidence({ trackDeviationM: null }))).toBe(
      'Start +40 s · end −2 min (±10.5 min) · no track to compare · match 94%',
    );
  });

  it('says when it is a part of the recording', () => {
    expect(describeEvidence(evidence({ segment: true }))).toMatch(
      /^the next part of the recording/,
    );
    expect(describeEvidence(evidence({ contained: true }))).toMatch(/within the recording$/);
  });

  it('says when the user merged it', () => {
    expect(describeEvidence(evidence({ userDecision: true }))).toBe('merged by you');
  });

  it('describes the fusion service status', () => {
    const now = new Date(2026, 9, 24, 12, 0);
    expect(fusionBusy(status())).toBe(false);
    expect(fusionBusy(status({ pending: 2 }))).toBe(true);
    expect(fusionBusy(status({ running: true }))).toBe(true);
    expect(fusionBusy(null)).toBe(false);
    expect(describeFusionStatus(status({ pending: 4, running: true }), now)).toBe(
      'Working… 4 windows queued',
    );
    expect(describeFusionStatus(status({ pending: 1 }), now)).toBe('Working… 1 window queued');
    expect(describeFusionStatus(status({ running: true }), now)).toBe('Working…');
    expect(describeFusionStatus(status(), now)).toBe('Up to date');
    expect(
      describeFusionStatus(
        status({
          lastRunAt: new Date(2026, 9, 24, 10, 32).toISOString(),
          lastResult: { activities: 174, fused: 12, conflicts: 156 },
        }),
        now,
      ),
    ).toBe('Up to date · last run 10:32 · 174 activities, 12 fused, 156 conflicts');
    expect(
      describeFusionStatus(status({ lastRunAt: new Date(2026, 8, 3, 9, 5).toISOString() }), now),
    ).toBe('Up to date · last run 3 Sep 09:05');
  });
});
