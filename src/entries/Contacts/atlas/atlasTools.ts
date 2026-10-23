/**
 * @fileoverview The Atlas assistant's tools (read the data, drive the map)
 * @module Contacts/atlas/atlasTools
 */

import { backendApiClient } from '../../../services/api/BackendApiClient';
import type { Toolset } from '../assistant/agent';
import type { AtlasQuery, AtlasSummary } from './types';
import { isSet, sourceLabel } from './types';

/** What the tools can do on screen */
export interface AtlasUi {
  /** Show a search on the map (sets the time range, area and types) */
  show: (q: AtlasQuery) => void;
  /** Open an activity's details */
  openActivity: (id: string) => void;
  /** Data summary for the prompt */
  summary: AtlasSummary | null;
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
      'One activity: distance, duration, climb, average/max heart rate, a heart-rate series (from the track or from health data at that time), the source records it is fused from (members), where each value comes from (fieldSources), conflicts between the sources, and the places visited at its start, end or during it.',
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
      '',
      'Rules:',
      '- Answer from tool results. To find a place by name use atlas_search with text; for coordinates you know (a city, a mountain) use atlas_near with a suitable radius.',
      '- Heart rate during an activity: atlas_activity gives it (from the track or from health data at the same minutes).',
      '- After a search the user may want to see, call show_on_map with the same filters.',
      '- Refer to an activity as [name](atlas:activity/ID) so the user can open it.',
      '- When the sources disagree (conflicts), say so and which value was used.',
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
