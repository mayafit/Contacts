/**
 * @fileoverview The Atlas assistant's tools (read the data, drive the map)
 * @module Contacts/atlas/atlasTools
 */

import { backendApiClient } from '../../../services/api/BackendApiClient';
import type { Toolset } from '../assistant/agent';
import type { ActivityAnalysis, AtlasQuery, AtlasSummary, Insights } from './types';
import { isSet, sourceLabel } from './types';

/** What the tools can do on screen */
export interface AtlasUi {
  /** Show a search on the map (sets the time range, area and types) */
  show: (q: AtlasQuery) => void;
  /** Open an activity's details */
  openActivity: (id: string) => void;
  /** Data summary for the prompt */
  summary: AtlasSummary | null;
  /** The filters the user sees now (for the prompt) */
  filters?: () => AtlasQuery;
}

const S = (description: string) => ({ type: 'string', description });
const N = (description: string) => ({ type: 'number', description });

const QUERY = {
  from: S('Start, ISO 8601'),
  to: S('End, ISO 8601'),
  minLat: N('South'),
  minLon: N('West'),
  maxLat: N('North'),
  maxLon: N('East'),
  types: {
    type: 'array',
    items: { type: 'string' },
    description: 'ride, hike, run, walk, drive, flight, transit, ski, swim, other',
  },
  text: S('Text in activity names/notes or place names/addresses'),
};

const DECLARATIONS = [
  {
    name: 'atlas_search',
    description:
      'Search activities and place visits; filters combine (time range, bounding box, types, text). Newest first.',
    parameters: {
      type: 'object',
      properties: { ...QUERY, limit: { type: 'integer', description: 'Default 50' } },
    },
  },
  {
    name: 'atlas_activity',
    description:
      'One activity: distance, duration, climb, average/max heart rate, a heart-rate series (from the track or from health data at that time), the source records it is fused from (members), where each value comes from (fieldSources), conflicts between the sources, how each member matches the primary (evidence: start/end time differences and track distance against the combined source tolerances, confidence 0..1) with the overall matchConfidence, the places visited at its start, end or during it, and the analysis of its track: moving/stopped time, moving average and top speed (pace in min/km for runs, walks and hikes), cadence, splits, minutes in heart-rate zones Z1–Z5 (shares of the max heart rate of the user), heart-rate drift (aerobic decoupling, second half against the first), best efforts with their all-time rank, and how it compares with earlier activities on the same route and with all activities of the same type over the past year.',
    parameters: { type: 'object', properties: { id: S('Activity id') }, required: ['id'] },
  },
  {
    name: 'atlas_health',
    description:
      'Health values over a time range (type heart_rate, steps, weight…), bucketed with min/max for long ranges.',
    parameters: {
      type: 'object',
      properties: { type: S('heart_rate, steps, …'), from: S('ISO 8601'), to: S('ISO 8601') },
      required: ['type', 'from', 'to'],
    },
  },
  {
    name: 'atlas_near',
    description:
      'Visits within a radius of a point and activities that passed through it. Use for "when was I at X / did I ride there".',
    parameters: {
      type: 'object',
      properties: {
        lat: N('Latitude'),
        lon: N('Longitude'),
        radiusM: N('Radius in metres (default 500)'),
        from: S('ISO 8601'),
        to: S('ISO 8601'),
      },
      required: ['lat', 'lon'],
    },
  },
  {
    name: 'atlas_identify_location',
    description:
      'What is at a point, from OpenStreetMap: the street address and the named places around it (shops, cafes, parks, sights…), nearest first, with their category, distance, address and website. Use it to tell what a visit or an unnamed place was. Can take a few seconds.',
    parameters: {
      type: 'object',
      properties: {
        lat: N('Latitude'),
        lon: N('Longitude'),
        radius: N('Search radius in metres, 20–300 (default 80)'),
      },
      required: ['lat', 'lon'],
    },
  },
  {
    name: 'atlas_insights',
    description:
      'Insights for a period and filters (time range, bounding box, types, text): totals with the period of the same length before, per type (count, km, hours, climb, avg speed, avg heart rate, longest), a series per day/week/month, activities per weekday and start hour, top places, records, streaks, travel (flights, days away), data quality (heart-rate coverage, recorded vs inferred, fused, conflicts, sources), proposals, the weekly training load (minutes × intensity, with hours and km), minutes in heart-rate zones over the period and per-sport trends (speed and heart-rate change, last third against the first third).',
    parameters: {
      type: 'object',
      properties: {
        from: QUERY.from,
        to: QUERY.to,
        minLat: QUERY.minLat,
        minLon: QUERY.minLon,
        maxLat: QUERY.maxLat,
        maxLon: QUERY.maxLon,
        types: QUERY.types,
        text: QUERY.text,
        conflictsOnly: {
          type: 'boolean',
          description: 'Only activities whose sources disagree (the "Sources disagree" filter)',
        },
      },
    },
  },
  {
    name: 'atlas_summary',
    description: 'Totals per activity type, date range of the data, imports.',
    parameters: { type: 'object', properties: {} },
  },
  {
    name: 'show_on_map',
    description:
      'Show results on the map and timeline: sets the time range, area and types the user sees.',
    parameters: { type: 'object', properties: QUERY },
  },
  {
    name: 'open_activity',
    description: "Open an activity's details on screen (track on the map, heart-rate chart).",
    parameters: { type: 'object', properties: { id: S('Activity id') }, required: ['id'] },
  },
];

const round = (n: number | null | undefined, d = 0) =>
  !isSet(n) ? undefined : Number(n.toFixed(d));

/**
 * The filters the user sees, as one line for the prompt.
 * @param f - The filters
 * @returns e.g. "from 2026-07-24T00:00:00.000Z, to …, types ride"; "none" when there are none
 */
const describeFilters = (f: AtlasQuery | undefined): string => {
  if (!f) {
    return 'none';
  }
  const parts = [
    f.from ? `from ${f.from}` : '',
    f.to ? `to ${f.to}` : '',
    isSet(f.minLat) && isSet(f.minLon) && isSet(f.maxLat) && isSet(f.maxLon)
      ? `area minLat ${round(f.minLat, 4)}, minLon ${round(f.minLon, 4)}, maxLat ${round(f.maxLat, 4)}, maxLon ${round(f.maxLon, 4)}`
      : '',
    f.types?.length ? `types ${f.types.join(', ')}` : '',
    f.text ? `text "${f.text}"` : '',
    f.conflictsOnly ? 'only activities whose sources disagree' : '',
  ].filter(Boolean);
  return parts.length ? parts.join('; ') : 'none';
};

/** Splits listed in full up to this many; longer activities get a summary */
const MAX_SPLITS = 20;

/**
 * An activity's analysis in a compact form for the assistant.
 * @param a - The analysis
 * @returns The same, rounded; the splits summarised for long activities
 */
const compactAnalysis = (a: ActivityAnalysis) => {
  const split = (s: ActivityAnalysis['splits'][number]) => ({
    n: s.index,
    km: round(s.distanceM / 1000, 2),
    time: Math.round(s.timeS),
    kmh: round(s.speedKmh, 1),
    paceMinKm: a.pace ? round(s.paceSPerKm / 60, 2) : undefined,
    hr: round(s.avgHr),
    climbM: round(s.climbM),
  });
  const full = a.splits.filter((s) => s.distanceM >= a.splitM * 0.5);
  const byKmh = [...full].sort((x, y) => y.speedKmh - x.speedKmh);
  const similar = a.similar;
  return {
    movingMin: round(a.movingS / 60, 1),
    stoppedMin: round(a.stoppedS / 60, 1),
    distanceKm: round(a.distanceM / 1000, 2),
    avgMovingKmh: round(a.avgMovingSpeedKmh, 1),
    maxKmh30s: round(a.maxSpeedKmh, 1),
    avgCadence: round(a.avgCadence),
    showPace: a.pace,
    splitKm: round(a.splitM / 1000, 2),
    splits: a.splits.length <= MAX_SPLITS ? a.splits.map(split) : undefined,
    splitsSummary:
      a.splits.length > MAX_SPLITS
        ? {
            count: a.splits.length,
            fastest: byKmh[0] ? split(byKmh[0]) : undefined,
            slowest: byKmh.length ? split(byKmh[byKmh.length - 1]) : undefined,
            first: split(a.splits[0]),
            last: split(a.splits[a.splits.length - 1]),
          }
        : undefined,
    hrZones: a.zones
      ? {
          maxHr: Math.round(a.zones.maxHr),
          maxHrFrom: a.zones.maxHrSource,
          lowerBoundsZ2toZ5: a.zones.bounds.map((b) => Math.round(b)),
          minutesZ1toZ5: a.zones.seconds.map((x) => round(x / 60, 1)),
        }
      : undefined,
    drift: a.drift
      ? {
          pct: round(a.drift.decouplingPct, 1),
          verdict: a.drift.verdict,
          firstHalf: {
            kmh: round(a.drift.firstHalf.speedKmh, 1),
            hr: round(a.drift.firstHalf.avgHr),
          },
          secondHalf: {
            kmh: round(a.drift.secondHalf.speedKmh, 1),
            hr: round(a.drift.secondHalf.avgHr),
          },
        }
      : undefined,
    bestEfforts: a.bestEfforts.map((e) => ({
      km: round(e.distanceM / 1000, 3),
      time: Math.round(e.timeS),
      kmh: round(e.speedKmh, 1),
      paceMinKm: a.pace ? round(e.paceSPerKm / 60, 2) : undefined,
      startOffsetS: Math.round(e.startOffsetS),
      allTimeRank: e.allTimeRank ?? undefined,
    })),
    similar: similar
      ? {
          routeRankBySpeed: similar.rankBySpeed ?? undefined,
          routeActivities: similar.ofRoute,
          routeAvgKmh: round(similar.avgSpeedKmh, 1),
          routeAvgHr: round(similar.avgHr),
          sameRoute: similar.sameRoute.slice(0, 10).map((r) => ({
            id: r.id,
            start: r.startTime,
            minutes: round(r.durationS / 60, 1),
            kmh: round(r.speedKmh, 1),
            hr: round(r.avgHr),
          })),
          sameTypePastYear: {
            count: similar.sameType.count,
            fasterThanPct: round(similar.sameType.speedPercentile),
            longerThanPct: round(similar.sameType.distancePercentile),
          },
        }
      : undefined,
  };
};

/**
 * The insights in a compact form for the assistant.
 * @param r - Insights
 * @returns The same, rounded, with the series as totals per bucket
 */
const compactInsights = (r: Insights) => {
  const buckets = new Map<string, { km: number; hours: number; count: number }>();
  r.series.points.forEach((p) => {
    const b = buckets.get(p.t) ?? { km: 0, hours: 0, count: 0 };
    b.km += p.distanceKm;
    b.hours += p.hours;
    b.count += p.count;
    buckets.set(p.t, b);
  });
  return {
    from: r.from,
    to: r.to,
    totals: r.totals,
    previousPeriod: r.previous ?? undefined,
    byType: r.byType.map((t) => ({
      type: t.type,
      count: t.count,
      km: round(t.distanceKm, 1),
      hours: round(t.hours, 1),
      climbM: round(t.climbM),
      avgKmh: round(t.avgSpeedKmh, 1),
      avgHr: round(t.avgHeartRate),
      longestKm: round(t.longestKm, 1),
    })),
    series: {
      bucket: r.series.bucket,
      points: [...buckets.entries()].slice(-60).map(([t, b]) => ({
        t,
        km: round(b.km, 1),
        hours: round(b.hours, 1),
        count: b.count,
      })),
    },
    byWeekdayMonFirst: r.byWeekday,
    byHour: r.byHour,
    topPlaces: r.topPlaces.map((p) => ({
      name: p.name,
      visits: p.visits,
      hours: round(p.hours, 1),
      firstVisit: p.firstVisit.slice(0, 10),
      new: p.isNew || undefined,
      unnamed: p.unnamed || undefined,
      lat: round(p.lat, 4),
      lon: round(p.lon, 4),
    })),
    records: r.records.map((x) => ({
      label: x.label,
      value: x.value,
      date: x.date.slice(0, 10),
      activityId: x.activityId,
    })),
    streaks: r.streaks,
    travel: { ...r.travel, flightKm: round(r.travel.flightKm) },
    coverage: {
      ...r.coverage,
      sources: r.coverage.sources.map((s) => ({
        source: sourceLabel(s.source),
        activities: s.activities,
      })),
    },
    proposals: r.proposals.map((p) => ({ severity: p.severity, title: p.title, text: p.text })),
    weeklyLoad: (r.weekly ?? []).slice(-26).map((w) => ({
      // Monday in the user's zone (the value is its midnight as UTC)
      week: new Date(w.week).toLocaleDateString('sv-SE'),
      load: round(w.load),
      hours: round(w.hours, 1),
      km: round(w.distanceKm, 1),
    })),
    hrZoneMinutesZ1toZ5: r.zoneSeconds ? r.zoneSeconds.map((x) => round(x / 60)) : undefined,
    trends: (r.trends ?? []).map((t) => ({
      type: t.type,
      activities: t.points.length,
      speedChangePct: round(t.speedChangePct, 1),
      hrChangePct: round(t.hrChangePct, 1),
    })),
  };
};

/**
 * The Atlas toolset for the assistant panel.
 * @param ui - Screen actions and the data summary
 * @returns Toolset
 */
export const atlasToolset = (ui: AtlasUi): Toolset => ({
  declarations: DECLARATIONS,
  systemPrompt: () => {
    const s = ui.summary;
    const offset = -new Date().getTimezoneOffset() / 60;
    return [
      "You are the assistant of the user's Atlas: their trips, places visited (Google Timeline), workouts (rides, hikes, runs from Sports Tracker and others) and health data (heart rate…).",
      'Activities are fused: one real-world activity recorded by several sources (e.g. Sports Tracker, Google Timeline, Fitbit) is one activity with several members. atlas_activity says which source supplied each value and where the sources disagree, so you can explain where a number comes from.',
      `Today is ${new Date().toISOString().slice(0, 10)}; the user's time zone is UTC${offset >= 0 ? '+' : ''}${offset}. Times from tools are UTC: convert when you show them.`,
      s
        ? `Data: ${s.activities} activities (${
            Object.entries(s.byType)
              .map(([k, v]) => `${v.count} ${k}`)
              .join(', ') || 'none'
          }), ${s.visits} visits to ${s.places} places, ${s.healthSamples} health values, from ${s.first?.slice(0, 10) ?? '?'} to ${s.last?.slice(0, 10) ?? '?'}.`
        : 'The data summary is not loaded yet; call atlas_summary.',
      `Filters on screen: ${describeFilters(ui.filters?.())}.`,
      '',
      'Rules:',
      '- Answer from tool results. To find a place by name use atlas_search with text; for coordinates you know (a city, a mountain) use atlas_near with a suitable radius.',
      '- Heart rate during an activity: atlas_activity gives it (from the track or from health data at the same minutes).',
      '- After a search the user may want to see, call show_on_map with the same filters.',
      '- For overviews, trends, comparisons with the period before, records, habits (weekdays, hours), places, streaks, travel or data quality, call atlas_insights; when the user asks about "my insights" or "this period" without saying which, use the filters on screen.',
      '- Refer to an activity as [name](atlas:activity/ID) so the user can open it.',
      '- When the sources disagree (conflicts), say so and which value was used.',
      '- You can identify what is at a place (a visit, an unnamed place, any coordinates): atlas_identify_location gives the street address and the named places nearby (OpenStreetMap), nearest first. Say how far the likely match is; when several are close, list the top few.',
      "- You can explain an activity's pacing (splits, moving vs stopped time, best efforts), heart-rate zones (Z1 under 60% of the user's max … Z5 from 90%), heart-rate drift (under 5% steady: the effort held up; more means fatigue, heat or too fast a start) and progress on a route (its rank and earlier times), from atlas_activity's analysis. For training load over weeks, time in zones over a period and per-sport speed/heart-rate trends, use atlas_insights.",
      '- To explain why records were combined, use the evidence of atlas_activity (time and track differences within the tolerances, match confidence; mergedByUser means the user merged them).',
      '- Reply in the language the user writes in. Be brief.',
    ].join('\n');
  },
  execute: async (name, args) => {
    const q: AtlasQuery = {
      from: args.from as string | undefined,
      to: args.to as string | undefined,
      minLat: args.minLat as number | undefined,
      minLon: args.minLon as number | undefined,
      maxLat: args.maxLat as number | undefined,
      maxLon: args.maxLon as number | undefined,
      types: args.types as string[] | undefined,
      text: args.text as string | undefined,
      conflictsOnly: args.conflictsOnly === true || undefined,
    };
    switch (name) {
      case 'atlas_search': {
        const r = await backendApiClient.atlasSearch({
          ...q,
          limit: Math.min(Number(args.limit) || 50, 200),
        });
        return {
          totalActivities: r.totalActivities,
          totalVisits: r.totalVisits,
          activities: r.activities.map((a) => ({
            id: a.id,
            type: a.type,
            name: a.name,
            start: a.startTime,
            end: a.endTime,
            distanceKm: round((a.distanceM ?? 0) / 1000, 1),
            minutes: round((a.durationS ?? 0) / 60),
            climbM: round(a.elevationGainM),
            avgHr: a.avgHeartRate ?? undefined,
            maxHr: a.maxHeartRate ?? undefined,
            sources: (a.sources ?? [a.source]).map((x) => sourceLabel(x)),
            memberCount: a.memberCount,
            conflicts: a.conflictCount || undefined,
            area: isSet(a.minLat)
              ? [round(a.minLat, 3), round(a.minLon, 3), round(a.maxLat, 3), round(a.maxLon, 3)]
              : undefined,
          })),
          visits: r.visits.map((v) => ({
            id: v.id,
            place: v.placeName,
            address: v.address,
            start: v.startTime,
            end: v.endTime,
            lat: round(v.lat, 4),
            lon: round(v.lon, 4),
          })),
        };
      }
      case 'atlas_activity': {
        const d = await backendApiClient.atlasActivity(String(args.id), 200);
        const hr = d.heartRate;
        const step = Math.max(1, Math.ceil(hr.length / 60));
        return {
          ...d.summary,
          polyline: undefined,
          notes: d.notes,
          heartRateSource: d.heartRateSource,
          heartRate: hr
            .filter((_, i) => i % step === 0)
            .map((p) => ({ t: p.t, bpm: Math.round(p.v) })),
          start: d.points[0] ? { lat: d.points[0].lat, lon: d.points[0].lon } : undefined,
          end: d.points.length
            ? { lat: d.points[d.points.length - 1].lat, lon: d.points[d.points.length - 1].lon }
            : undefined,
          members: (d.members ?? []).map((m) => ({
            id: m.id,
            source: sourceLabel(m.source),
            primary: m.primary,
            type: m.type,
            name: m.name ?? undefined,
            start: m.startTime,
            end: m.endTime,
            distanceKm: isSet(m.distanceM) ? round(m.distanceM / 1000, 1) : undefined,
            minutes: isSet(m.durationS) ? round(m.durationS / 60) : undefined,
            avgHr: m.avgHeartRate ?? undefined,
            maxHr: m.maxHeartRate ?? undefined,
            hasTrack: m.pointCount > 0,
          })),
          fieldSources: Object.fromEntries(
            Object.entries(d.fieldSources ?? {}).map(([k, v]) => [k, sourceLabel(v)]),
          ),
          matchConfidence: isSet(d.matchConfidence) ? round(d.matchConfidence, 2) : undefined,
          evidence: (d.evidence ?? []).map((e) => ({
            memberId: e.memberId,
            source: sourceLabel(e.source),
            startDeltaS: Math.round(e.startDeltaS),
            endDeltaS: Math.round(e.endDeltaS),
            timeToleranceS: Math.round(e.timeToleranceS),
            trackDeviationM: isSet(e.trackDeviationM) ? Math.round(e.trackDeviationM) : null,
            locationToleranceM: Math.round(e.locationToleranceM),
            confidence: round(e.confidence, 2),
            mergedByUser: e.userDecision || undefined,
          })),
          conflicts: (d.conflicts ?? []).map((c) => ({
            field: c.field,
            message: c.message,
            values: c.values.map((v) => ({ source: sourceLabel(v.source), value: v.value })),
            used: c.chosen,
          })),
          places: (d.places ?? []).map((p) => ({
            role: p.role,
            place: p.placeName ?? undefined,
            address: p.address ?? undefined,
            start: p.startTime,
            end: p.endTime,
            lat: round(p.lat, 4),
            lon: round(p.lon, 4),
          })),
          analysis: d.analysis ? compactAnalysis(d.analysis) : undefined,
        };
      }
      case 'atlas_health': {
        const r = await backendApiClient.atlasHealth(
          String(args.type),
          String(args.from),
          String(args.to),
          120,
        );
        return {
          points: r.map((p) => ({
            t: p.t,
            v: round(p.v, 1),
            min: round(p.min),
            max: round(p.max),
          })),
        };
      }
      case 'atlas_near': {
        const r = await backendApiClient.atlasNear(
          Number(args.lat),
          Number(args.lon),
          Number(args.radiusM) || 500,
          q.from,
          q.to,
        );
        return {
          visits: r.visits
            .slice(0, 50)
            .map((v) => ({ id: v.id, place: v.placeName, start: v.startTime, end: v.endTime })),
          activities: r.activities.slice(0, 50).map((a) => ({
            id: a.id,
            type: a.type,
            name: a.name,
            start: a.startTime,
            distanceKm: round((a.distanceM ?? 0) / 1000, 1),
            sources: (a.sources ?? [a.source]).map((x) => sourceLabel(x)),
            memberCount: a.memberCount,
          })),
        };
      }
      case 'atlas_identify_location': {
        const radius = Number(args.radius);
        const r = await backendApiClient.atlasLookup(
          Number(args.lat),
          Number(args.lon),
          Number.isFinite(radius) && radius > 0 ? Math.min(Math.max(radius, 20), 300) : undefined,
        );
        return {
          address: r.address?.display ?? null,
          candidates: r.candidates.slice(0, 10).map((c) => ({
            name: c.localName && c.localName !== c.name ? `${c.name} (${c.localName})` : c.name,
            label: c.label,
            distanceM: Math.round(c.distanceM),
            address: c.address ?? undefined,
            website: c.website ?? undefined,
          })),
          errors: r.errors.length ? r.errors : undefined,
          attribution: r.attribution,
        };
      }
      case 'atlas_insights': {
        const r = await backendApiClient.atlasInsights(
          q,
          Intl.DateTimeFormat().resolvedOptions().timeZone,
        );
        return compactInsights(r);
      }
      case 'atlas_summary': {
        const s = await backendApiClient.atlasSummary();
        return { ...s, imports: s.imports.slice(0, 10) };
      }
      case 'show_on_map':
        ui.show(q);
        return { status: 'shown' };
      case 'open_activity':
        ui.openActivity(String(args.id));
        return { status: 'opened' };
      default:
        return { error: `Unknown tool ${name}` };
    }
  },
});
