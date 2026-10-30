/**
 * @fileoverview Atlas data shapes (match the backend's Atlas DTOs)
 * @module Contacts/atlas/types
 */

/** A track point to import */
export interface PointInput {
  t: string;
  lat: number;
  lon: number;
  ele?: number | null;
  hr?: number | null;
  speed?: number | null;
  cad?: number | null;
}

/** An activity to import */
export interface ActivityInput {
  externalId?: string | null;
  type: string;
  name?: string | null;
  startTime?: string | null;
  endTime?: string | null;
  distanceM?: number | null;
  durationS?: number | null;
  elevationGainM?: number | null;
  notes?: string | null;
  points?: PointInput[];
  /** The source's own summary, used when the track has no heart rate */
  avgHeartRate?: number | null;
  maxHeartRate?: number | null;
}

/** A place to import */
export interface PlaceInput {
  externalId?: string | null;
  name: string;
  lat: number;
  lon: number;
  address?: string | null;
  category?: string | null;
  googlePlaceId?: string | null;
}

/** A visit to import */
export interface VisitInput {
  externalId?: string | null;
  startTime: string;
  endTime: string;
  lat: number;
  lon: number;
  placeExternalId?: string | null;
  placeName?: string | null;
  confidence?: number | null;
}

/** A health sample to import */
export interface HealthInput {
  type: string;
  startTime: string;
  value: number;
  endTime?: string | null;
  unit?: string | null;
}

/** One of Google's questions about a place, and the Owner's answer */
export interface ReviewAnswer {
  question: string;
  answer: string;
}

/** A Google Maps review in an import batch (AD-39): the published values, else the draft's */
export interface ReviewInput {
  name: string;
  address?: string | null;
  countryCode?: string | null;
  lat: number;
  lon: number;
  /** 1 to 5 */
  rating?: number | null;
  text?: string | null;
  /** False: only a draft was saved */
  published: boolean;
  /** When it was written or last changed (ISO) */
  reviewedAt: string;
  mapsUrl?: string | null;
  answers?: ReviewAnswer[];
}

/** What one parsed file contributes */
export interface ParsedData {
  source: string;
  activities: ActivityInput[];
  places: PlaceInput[];
  visits: VisitInput[];
  health: HealthInput[];
  /** Google Maps reviews (Takeout Reviews.json) */
  reviews?: ReviewInput[];
}

/** An activity as listed (no points) */
export interface ActivitySummary {
  id: string;
  type: string;
  name?: string | null;
  startTime: string;
  endTime: string;
  distanceM?: number | null;
  durationS?: number | null;
  elevationGainM?: number | null;
  avgHeartRate?: number | null;
  maxHeartRate?: number | null;
  source: string;
  polyline?: string | null;
  minLat?: number | null;
  minLon?: number | null;
  maxLat?: number | null;
  maxLon?: number | null;
  pointCount: number;
  /** Sources of the fused activity's members, e.g. ["sports-tracker", "fitbit"] */
  sources: string[];
  /** Source records fused into this activity (1 when not fused) */
  memberCount: number;
  /** Fields the sources disagree on (0 when none) */
  conflictCount: number;
  /** Photos taken during it (search results; missing from older servers) */
  photoCount?: number;
  /**
   * 'matched' when polyline is a road-matched route chosen by the server (AD-34);
   * 'recorded' (or missing, older servers) for the recorded line
   */
  geometry?: RouteGeometry;
  /** The matched line's pieces in metres along it (the detail view only) */
  pieces?: RoutePiece[] | null;
  /** The matched line's length (the detail view only) */
  onRoadsM?: number | null;
}

/** Which line an activity is drawn with */
export type RouteGeometry = 'matched' | 'recorded';

/** One piece of a matched line: along recorded points, chosen by routing, or not matched */
export interface RoutePiece {
  fromM: number;
  toM: number;
  kind: 'observed' | 'inferred' | 'gap';
}

/** The quality, stored match and decision of the activity's (primary) record */
export interface RouteDescription {
  /** good, fair, poor; null when not assessed or without a track */
  grade: 'good' | 'fair' | 'poor' | null;
  score: number | null;
  flags: string[];
  /** yes, no_track, type_never, outside_coverage, rail, unknown; null when not assessed */
  matchable: string | null;
  needsImprovement: boolean;
  /** matched, low_confidence, failed; null without a stored match */
  status: 'matched' | 'low_confidence' | 'failed' | null;
  confidence: number | null;
  matchedDistanceM: number | null;
  /** The stored match is for the record as it is now */
  current: boolean;
  decision: 'reject' | 'accept' | null;
  reason: string | null;
  /** How the stored match was made (AD-38); missing from older servers */
  method?: 'auto' | 'similar' | 'waypoints' | null;
  /** The points the Owner marked for this route, [lat, lon] in order; null without them */
  waypoints?: [number, number][] | null;
}

/** The similar drive a rebuild borrowed from (AD-38) */
export interface RebuildBasis {
  activityId: string;
  start: string;
  reversed: boolean;
  /** Share of this drive's own points along the similar one (0-1) */
  fit: number;
  /** Drives near this drive's points that were scored */
  candidates: number;
  /** Of those, the ones this drive's points follow */
  agreeing: number;
  /** Of the best of those, the ones that take the basis's way */
  sameWay?: number;
  examples: { activityId: string; start: string; reversed: boolean }[];
}

/** A rebuild's result: a suggestion to use or discard (AD-38) */
export interface RebuildSuggestion {
  method: 'similar' | 'waypoints';
  outcome: 'suggested' | 'no_similar' | 'no_match' | 'invalid' | 'not_matchable' | 'gone';
  /** Why there is no suggestion */
  message?: string | null;
  activityId: string;
  status?: 'matched' | 'low_confidence' | 'failed' | null;
  confidence?: number | null;
  /** The stored match's confidence before (null without one) */
  previousConfidence?: number | null;
  reason?: string | null;
  polyline?: string | null;
  pieces?: RoutePiece[] | null;
  roads?: RouteRoad[] | null;
  lengthM?: number | null;
  basis?: RebuildBasis | null;
  waypoints?: [number, number][] | null;
  /** Synthetic points added */
  added?: number;
}

/** A road a matched route follows (AD-37) */
export interface RouteRoad {
  /** A route number ("431") or a street name, as the map data spells it */
  name: string;
  /** A route number */
  ref: boolean;
  /** Metres of the route on it */
  m: number;
  /** Where the route first takes it, in metres along the route */
  fromM: number;
}

/** Route quality in the detail view (missing when route quality is off) */
export interface ActivityRouteInfo {
  /** The records whose track is drawn: what "Fix this route" queues */
  recordIds: string[];
  /** Null before the route service assessed it, or when it cannot be read */
  description: RouteDescription | null;
  observedShare: number | null;
  inferredShare: number | null;
  /** The roads the drawn matched route follows, in travel order; missing when it is not drawn */
  roads?: RouteRoad[] | null;
}

/** A route job's progress (GET /api/routes/jobs/{id}) */
export interface RouteJob {
  id: string;
  kind: string;
  priority: string;
  state: 'queued' | 'running' | 'done' | 'failed';
  total: number;
  done: number;
  lastError?: string | null;
  /** A rebuild's suggestion (AD-38) */
  result?: RebuildSuggestion | null;
}

/** What the route service is doing for the Owner (GET /api/routes/status) */
export interface RouteStatus {
  /** false when the service cannot be reached (or is not enabled) */
  available?: boolean;
  /** false when route quality is switched off (nothing to show) */
  enabled?: boolean;
  message?: string;
  queued?: { interactive: number; batch: number };
  running?: number;
  retrying?: number;
  waitingForMatch?: number;
  etaSeconds?: number;
  lastRunAt?: string | null;
  lastError?: string | null;
  backfilled?: boolean;
  engine?: {
    up: boolean;
    version?: string | null;
    breaker?: { open: boolean; openUntil?: string | null; consecutiveFailures?: number };
  };
  graphVersion?: string | null;
  regions?: string[];
}

/** One source record contributing to a fused activity */
export interface ActivityMember {
  id: string;
  source: string;
  type: string;
  name: string | null;
  startTime: string;
  endTime: string;
  distanceM: number | null;
  durationS: number | null;
  avgHeartRate: number | null;
  maxHeartRate: number | null;
  pointCount: number;
  primary: boolean;
  importId: string | null;
}

/** A field the sources of a fused activity disagree on */
export interface FusionConflict {
  /** 'location': the tracks deviate beyond the combined location tolerance */
  field: 'type' | 'distanceM' | 'startTime' | 'endTime' | 'track' | 'location';
  /** Human text, e.g. "Sports Tracker says Ride, Google Timeline says Drive" */
  message: string;
  values: { source: string; value: string }[];
  /** The value used */
  chosen: string;
}

/** A place visit linked to an activity */
export interface LinkedPlace {
  visitId: string;
  placeName: string | null;
  address: string | null;
  role: 'start' | 'end' | 'during';
  startTime: string;
  endTime: string;
  lat: number;
  lon: number;
}

/** A connected account that Atlas syncs from */
export interface SourceConnection {
  provider: 'sports-tracker' | 'onedrive';
  /** Username or email as entered */
  account: string | null;
  status: 'idle' | 'syncing' | 'error' | 'expired';
  lastSyncAt: string | null;
  lastError: string | null;
  /** Workouts found in the last sync */
  itemsTotal: number;
  /** Workouts processed so far */
  itemsDone: number;
  /** New or updated in the last sync */
  itemsImported: number;
}

/** Result of a fusion run */
export interface FuseResult {
  activities: number;
  fused: number;
  conflicts: number;
}

/** How a non-primary member of a fused activity matches the primary */
export interface MatchEvidence {
  memberId: string;
  source: string;
  /** Member start − primary start (seconds, signed) */
  startDeltaS: number;
  /** Member end − primary end (seconds, signed) */
  endDeltaS: number;
  /** Combined time tolerance used (seconds) */
  timeToleranceS: number;
  /** Median distance between the tracks at the same moments; null when either has no track */
  trackDeviationM: number | null;
  /** Combined location tolerance used (meters) */
  locationToleranceM: number;
  /** 0..1 */
  confidence: number;
  /** The user merged it (no automatic evidence needed) */
  userDecision: boolean;
  /** The next part of the same recording (stopped and started again); missing from older servers */
  segment?: boolean;
  /** A piece lying within one of the activity's recordings (a phone's segment of it); missing from older servers */
  contained?: boolean;
}

/** How accurate one source's times and positions are */
export interface SourceTolerance {
  /** 'sports-tracker' | 'gpx' | 'tcx' | 'manual' | 'google-fit' | 'fitbit' | 'google-timeline' | 'other' */
  source: string;
  /** 1 .. 21600 */
  timeToleranceS: number;
  /** 1 .. 20000 */
  locationToleranceM: number;
  /** Equals the built-in default */
  isDefault: boolean;
}

/** The owner's fusion settings */
export interface FusionSettings {
  /** 0..1: lowest confidence for an automatic match */
  matchThreshold: number;
  /** Every known source, in trust order */
  sources: SourceTolerance[];
}

/** State of the background fusion service for this owner */
export interface FusionStatus {
  /** Queued time windows */
  pending: number;
  running: boolean;
  lastRunAt: string | null;
  lastResult: FuseResult | null;
  /** Set when the last run failed (the service retries it later) */
  lastError?: string | null;
}

/** A visit with its place */
export interface VisitSummary {
  id: string;
  placeId?: string | null;
  placeName?: string | null;
  address?: string | null;
  startTime: string;
  endTime: string;
  lat: number;
  lon: number;
  source: string;
  /** How the place got its identity (see IdentifyState); missing from older servers */
  identifyState?: string;
  /** Photos taken during it (search results; missing from older servers) */
  photoCount?: number;
}

/**
 * How a place got its identity:
 * - pending: not looked at yet by the background identification
 * - auto: named automatically (one clear establishment, or from a nearby place)
 * - ambiguous: several possible identities (the place's candidates); the user picks
 * - none: nothing nameable found around it
 * - user: the user named or confirmed it
 */
export type IdentifyState = 'pending' | 'auto' | 'ambiguous' | 'none' | 'user';

/** A place with what is known about it and its visits */
export interface PlaceDetail {
  id: string;
  name: string;
  address: string | null;
  /** Human label, e.g. "Cafe", or a source's own category */
  category: string | null;
  lat: number;
  lon: number;
  notes: string | null;
  /** Where the place came from (google-timeline, manual…) */
  source: string;
  /** The real-world location it is associated with, e.g. "osm:node/123456" */
  linkedRef: string | null;
  website: string | null;
  phone: string | null;
  /** OpenStreetMap opening_hours syntax, shown as is */
  openingHours: string | null;
  /** Anything else found (cuisine, brand, wikipedia, operator, …) */
  details: Record<string, string>;
  /** Visits to this place */
  visits: number;
  firstVisit: string | null;
  lastVisit: string | null;
  hoursSpent: number;
  /** How the place got its identity */
  identifyState: IdentifyState;
  /** The possible identities when ambiguous (at most 6, nearest first); else empty */
  candidates: LocationCandidate[];
  /** 'lookup', 'nearby' (copied from a nearby place the user identified) or 'review' (named by the Owner's review); null otherwise */
  identifiedFrom: string | null;
  /** The Owner's Google Maps review of the place (AD-39) */
  review?: PlaceReview | null;
  /** Where the place stands on reviews */
  reviewState?: ReviewState | null;
}

/** The Owner's Google Maps review of a place (AD-39) */
export interface PlaceReview {
  id: string;
  /** The place as Google names it */
  name: string;
  address: string | null;
  rating: number | null;
  text: string | null;
  /** False: a draft never published */
  published: boolean;
  reviewedAt: string;
  mapsUrl: string | null;
  answers: ReviewAnswer[];
  /** The Atlas place it is about, when linked */
  placeId: string | null;
  linkedBy: string | null;
  lat: number;
  lon: number;
}

/**
 * Where a place stands on reviews: reviewed; to_review; set aside (later until a date,
 * never — for the place, or its category — or done); not_reviewable and why
 */
export interface ReviewState {
  state: 'reviewed' | 'to_review' | 'later' | 'never' | 'done' | 'not_reviewable';
  until?: string | null;
  /** Never: set aside by its category */
  category?: boolean;
  /** not_reviewable: no_identity, category, no_recent_visit, routine */
  reason?: string | null;
  /** Opens the place on Google Maps, where the review is written */
  writeUrl?: string | null;
}

/** A place to review */
export interface ReviewDue {
  placeId: string;
  name: string;
  category: string | null;
  lastVisit: string;
  visits: number;
  writeUrl: string;
  lat: number;
  lon: number;
}

/** The Owner's reviews and the places to review (all time) */
export interface ReviewsOverview {
  written: number;
  average: number | null;
  linked: number;
  unlinked: number;
  recent: PlaceReview[];
  due: ReviewDue[];
  dueTotal: number;
}

/** A choice about a review reminder */
export type ReviewAction = 'later' | 'never' | 'done' | 'never_kind' | 'clear';

/** A place near an identified one that got (or may get) the same identity */
export interface NearbyPlace {
  placeId: string;
  /** Its current name ("Place" when unnamed) */
  name: string;
  lat: number;
  lon: number;
  /** From the identified place */
  distanceM: number;
  visits: number;
  identifyState: string;
}

/** A visit's place after naming or associating it */
export interface VisitPlaceResult {
  visit: VisitSummary;
  place: PlaceDetail;
  /** Nearby places that got the same identity automatically */
  applied: NearbyPlace[];
  /** Nearby places that may be the same; the user decides */
  suggested: NearbyPlace[];
}

/** The street address at a point */
export interface LookupAddress {
  /** One line, e.g. "12 Herzl St, Rehovot, Israel" */
  display: string;
  houseNumber: string | null;
  road: string | null;
  suburb: string | null;
  city: string | null;
  state: string | null;
  postcode: string | null;
  country: string | null;
  countryCode: string | null;
}

/** A named place near a looked-up point */
export interface LocationCandidate {
  /** e.g. "osm:node/123", "osm:way/456" */
  ref: string;
  /** In English when known, else the local name */
  name: string;
  /** The local-language name when it differs */
  localName: string | null;
  /** Raw OpenStreetMap key=value, e.g. "amenity=cafe" */
  kind: string;
  /** Human label, e.g. "Cafe" */
  label: string;
  lat: number;
  lon: number;
  /** From the looked-up point */
  distanceM: number;
  address: string | null;
  website: string | null;
  phone: string | null;
  openingHours: string | null;
  /** cuisine, brand, operator, wikipedia, wikidata, description, … */
  details: Record<string, string>;
}

/** What is at a point (OpenStreetMap) */
export interface LocationLookup {
  lat: number;
  lon: number;
  address: LookupAddress | null;
  /** Nearest first, at most 25 */
  candidates: LocationCandidate[];
  /** Failures of one source (partial results), e.g. "Nearby places are unavailable right now" */
  errors: string[];
  /** "© OpenStreetMap contributors" — shown with the results */
  attribution: string;
}

/** A visit's place set to a name/address, or associated with a real-world location */
export interface PlaceAssociation {
  name: string;
  address?: string | null;
  /** The candidate's label */
  category?: string | null;
  /** The candidate's ref; null for a manual name/address */
  linkedRef?: string | null;
  website?: string | null;
  phone?: string | null;
  openingHours?: string | null;
  details?: Record<string, string> | null;
}

/** Changes to a place (missing: unchanged; "" clears linkedRef, website, phone, openingHours) */
export interface PlacePatch {
  name?: string;
  address?: string;
  category?: string;
  notes?: string;
  linkedRef?: string;
  website?: string;
  phone?: string;
  openingHours?: string;
}

/** Search filters */
export interface AtlasQuery {
  from?: string;
  to?: string;
  minLat?: number;
  minLon?: number;
  maxLat?: number;
  maxLon?: number;
  types?: string[];
  text?: string;
  activities?: boolean;
  visits?: boolean;
  limit?: number;
  /** Only fused activities whose sources disagree */
  conflictsOnly?: boolean;
  /** Only activities whose matched route follows this road: a route number (431) or part of a street name */
  road?: string;
}

/** One track of the density map */
export interface DensityTrack {
  /** Activity type: ride, drive, flight, walk… */
  type: string;
  /** Google encoded polyline (precision 5), simplified */
  polyline: string;
}

/** Everything matching a query, light enough to draw at once (density map) */
export interface DensityData {
  /** Newest first, at most 6000 */
  tracks: DensityTrack[];
  /** [lat, lon, hours spent] per visit, at most 30000 */
  visits: [number, number, number][];
  /** More matched than returned */
  truncated: boolean;
}

/**
 * A photo or video of the connected OneDrive. lat/lon is where it is shown; placedBy says
 * how that was found: the camera's position (gps), the track at its time, or the visit.
 */
export interface PhotoSummary {
  id: string;
  name: string;
  takenAt: string;
  isVideo: boolean;
  lat: number | null;
  lon: number | null;
  placedBy: 'gps' | 'track' | 'visit' | null;
  width: number | null;
  height: number | null;
  /** Opens it in OneDrive */
  webUrl: string | null;
  activityId: string | null;
  visitId: string | null;
  camera: string | null;
}

/** Photos for the map (truncated: there were more; the newest are given) */
export interface PhotoMapData {
  photos: PhotoSummary[];
  truncated: boolean;
}

/** The OneDrive connection: whether the server can connect it, the connection, photo counts */
export interface OneDriveStatus {
  configured: boolean;
  connection: SourceConnection | null;
  photos: number;
  /** Placed on the map */
  withPosition: number;
}

/** Search results */
export interface SearchResult {
  activities: ActivitySummary[];
  visits: VisitSummary[];
  totalActivities: number;
  totalVisits: number;
}

/** A track point as returned */
export interface PointOutput {
  t: string;
  lat: number;
  lon: number;
  ele?: number | null;
  hr?: number | null;
  /** Meters per second (recorded, or derived and smoothed) */
  speed?: number | null;
  /** Cadence (rpm / steps per minute) when recorded */
  cad?: number | null;
  /** Distance from the start along the track, meters (may be missing from older servers) */
  d?: number;
}

/** One split of an activity (per km, per 5 km or per 100 m) */
export interface Split {
  /** 1-based */
  index: number;
  /** Usually ActivityAnalysis.splitM; the last one may be shorter */
  distanceM: number;
  /** Moving time over the split */
  timeS: number;
  speedKmh: number;
  /** Seconds per km */
  paceSPerKm: number;
  avgHr: number | null;
  /** Elevation gain within the split */
  climbM: number;
}

/** Time in heart-rate zones of one activity */
export interface HrZones {
  /** The user's own max (98th percentile of all max heart rates), or this activity's */
  maxHr: number;
  maxHrSource: 'history' | 'activity';
  /** 4 lower bounds (bpm) of zones 2..5: 60/70/80/90 % of maxHr */
  bounds: number[];
  /** 5 values: time in zone 1 (<60%) … zone 5 (≥90%) */
  seconds: number[];
  avgHr: number;
  maxHrHere: number;
}

/** Aerobic decoupling: speed/heart-rate ratio of the second half against the first */
export interface Drift {
  firstHalf: { speedKmh: number; avgHr: number };
  secondHalf: { speedKmh: number; avgHr: number };
  /** (ratio1 − ratio2) / ratio1 × 100; under 5 is steady */
  decouplingPct: number;
  /** Under 5, 5–10, over 10 */
  verdict: 'steady' | 'drifting' | 'fading';
}

/** A fastest stretch of an activity */
export interface BestEffort {
  /** run/walk/hike: 1000, 5000, 10000, 21097; ride: 5000, 10000, 20000, 40000 */
  distanceM: number;
  timeS: number;
  speedKmh: number;
  paceSPerKm: number;
  /** Where it started (seconds from the activity start) */
  startOffsetS: number;
  /** 1 = the user's fastest of this distance for this type; null when unknown */
  allTimeRank: number | null;
}

/** Another activity on the same route */
export interface SimilarActivity {
  id: string;
  startTime: string;
  durationS: number;
  speedKmh: number | null;
  avgHr: number | null;
  distanceM: number | null;
}

/** An activity against the user's other activities */
export interface Similar {
  /** The same route, newest first, at most 20, without this one */
  sameRoute: SimilarActivity[];
  /** 1 = fastest of this route (this one included) */
  rankBySpeed: number | null;
  /** Route activities counted (this one included) */
  ofRoute: number;
  /** Average of the route activities */
  avgSpeedKmh: number | null;
  avgHr: number | null;
  /** Percentiles (0–100) among the user's activities of the same type in the last 365 days */
  sameType: { count: number; speedPercentile: number | null; distancePercentile: number | null };
}

/** The analysis of an activity's track (computed on the server) */
export interface ActivityAnalysis {
  /** Time while moving */
  movingS: number;
  stoppedS: number;
  distanceM: number;
  avgMovingSpeedKmh: number | null;
  /** Highest 30-second average */
  maxSpeedKmh: number | null;
  avgCadence: number | null;
  /** Run, walk, hike: show pace (min/km) instead of speed */
  pace: boolean;
  splits: Split[];
  /** The split length used */
  splitM: number;
  /** Null without heart rate */
  zones: HrZones | null;
  /** Null without heart rate or under 20 minutes moving */
  drift: Drift | null;
  bestEfforts: BestEffort[];
  similar: Similar | null;
}

/** A value over time */
export interface SeriesPoint {
  t: string;
  v: number;
  min?: number | null;
  max?: number | null;
}

/** An activity with track and heart rate */
export interface ActivityDetail {
  summary: ActivitySummary;
  notes?: string | null;
  points: PointOutput[];
  heartRateSource?: 'track' | 'health' | 'none' | null;
  heartRate: SeriesPoint[];
  /** Every contributing source record, primary first */
  members: ActivityMember[];
  conflicts: FusionConflict[];
  /** Field → the source that supplied it, e.g. { distanceM: "sports-tracker" } */
  fieldSources: Record<string, string>;
  /** Visits linked to the activity */
  places: LinkedPlace[];
  /** One per non-primary member: how it matches the primary (may be missing from older servers) */
  evidence?: MatchEvidence[];
  /** Lowest member confidence (0..1); null when not fused */
  matchConfidence?: number | null;
  /** Null without a track of at least 2 points (missing from older servers) */
  analysis?: ActivityAnalysis | null;
  /** Route quality (missing when it is off or unavailable) */
  route?: ActivityRouteInfo | null;
}

/** Timeline histogram row */
export interface HistogramRow {
  bucket: string;
  kind: string;
  count: number;
}

/** One import run */
export interface ImportRun {
  id: string;
  source: string;
  fileName?: string | null;
  createdAt: string;
  activities: number;
  places: number;
  visits: number;
  healthSamples: number;
}

/** Totals */
export interface AtlasSummary {
  activities: number;
  places: number;
  visits: number;
  healthSamples: number;
  first?: string | null;
  last?: string | null;
  byType: Record<string, { count: number; distanceKm: number }>;
  imports: ImportRun[];
}

/** Visits and activities near a point */
export interface NearResult {
  visits: VisitSummary[];
  activities: ActivitySummary[];
}

/** Totals of a period (insights) */
export interface InsightsTotals {
  activities: number;
  /** Movement on own power and vehicles together */
  distanceKm: number;
  hours: number;
  climbM: number;
  visits: number;
  /** Distinct places visited */
  places: number;
}

/** Statistics of one activity type (insights) */
export interface InsightsTypeStats {
  /** ride, run, hike, walk, drive, flight, transit, ski, swim, other */
  type: string;
  count: number;
  distanceKm: number;
  hours: number;
  climbM: number;
  /** Over the activities with heart rate */
  avgHeartRate: number | null;
  avgSpeedKmh: number | null;
  longestKm: number | null;
}

/** Size of a series bucket (insights) */
export type InsightsBucket = 'day' | 'week' | 'month';

/** One activity type in one series bucket (insights) */
export interface InsightsSeriesPoint {
  /** Start of the bucket, ISO */
  t: string;
  type: string;
  distanceKm: number;
  hours: number;
  count: number;
}

/** The series over the period (insights) */
export interface InsightsSeries {
  bucket: InsightsBucket;
  points: InsightsSeriesPoint[];
}

/** A place with the time spent there (insights) */
export interface InsightsPlace {
  placeId: string | null;
  /** "Unnamed place" when generic */
  name: string;
  lat: number;
  lon: number;
  visits: number;
  hours: number;
  /** First visit ever (not only in the query) */
  firstVisit: string;
  lastVisit: string;
  /** The first visit ever falls inside the query */
  isNew: boolean;
  unnamed: boolean;
}

/** A personal best within the query (insights) */
export interface InsightsRecord {
  kind: 'longest' | 'longestTime' | 'climb' | 'fastest' | 'maxHeartRate';
  /** The activity type it applies to */
  type: string;
  /** e.g. "Longest ride" */
  label: string;
  /** Formatted, e.g. "87.4 km", "31.2 km/h", "178 bpm" */
  value: string;
  activityId: string;
  date: string;
}

/** Activity streaks (insights) */
export interface InsightsStreaks {
  longestActiveDays: number;
  longestActiveFrom: string | null;
  daysSinceLast: number | null;
  activeDays: number;
}

/** Travel (insights) */
export interface InsightsTravel {
  flights: number;
  flightKm: number;
  /** Days with a visit over 100 km from Home (0 when Home is unknown) */
  daysAway: number;
}

/** Activities of one source (insights) */
export interface InsightsSourceCount {
  source: string;
  activities: number;
}

/** How complete and consistent the data is (insights) */
export interface InsightsCoverage {
  /** Rides, runs, hikes, walks, swims and skis in the query */
  sports: number;
  /** Of the sports */
  withHeartRate: number;
  /** With a dedicated recording (Sports Tracker, GPX, TCX, Fit) */
  recorded: number;
  /** Only Google Timeline */
  inferredOnly: number;
  /** Combining several sources */
  fused: number;
  withConflicts: number;
  sources: InsightsSourceCount[];
  /** With an impossible speed for their type (probably misclassified; left out of records) */
  implausible?: number;
}

/** What a proposal's button does */
export type InsightsProposalAction =
  | { kind: 'activity'; activityId: string }
  | { kind: 'place'; lat: number; lon: number; name: string | null }
  | { kind: 'filter'; types?: string[]; conflictsOnly?: boolean }
  | { kind: 'fusion' }
  | { kind: 'import' };

/** A proposal derived from the insights */
export interface InsightsProposal {
  kind:
    | 'rhythm'
    | 'load-high'
    | 'load-low'
    | 'conflicts'
    | 'heart-rate'
    | 'unnamed-place'
    | 'new-places'
    | 'record'
    | 'away'
    | 'implausible';
  severity: 'info' | 'tip' | 'warning';
  /** Short, e.g. "18 days since your last ride" */
  title: string;
  /** One or two sentences */
  text: string;
  action: InsightsProposalAction | null;
}

/** Insights for a query */
export interface Insights {
  /** The query's time range (null when open) */
  from: string | null;
  to: string | null;
  totals: InsightsTotals;
  /** The period of the same length right before (only with from and to) */
  previous: InsightsTotals | null;
  /** Most time first */
  byType: InsightsTypeStats[];
  series: InsightsSeries;
  /** 7 counts of activities, Monday first (local time) */
  byWeekday: number[];
  /** 24 counts of activity starts (local time) */
  byHour: number[];
  /** Up to 10, most time first */
  topPlaces: InsightsPlace[];
  records: InsightsRecord[];
  streaks: InsightsStreaks;
  travel: InsightsTravel;
  coverage: InsightsCoverage;
  /** Most important first */
  proposals: InsightsProposal[];
  /** Training load per week (only with from and to), oldest first; missing from older servers */
  weekly?: WeekLoad[];
  /** 5 values: time in heart-rate zones over the period (null without any heart rate) */
  zoneSeconds?: number[] | null;
  /** Per sport type with at least 3 activities in the period */
  trends?: TypeTrend[];
}

/** Training of one week (insights) */
export interface WeekLoad {
  /** Monday (local), ISO */
  week: string;
  hours: number;
  /** Without flights and drives */
  distanceKm: number;
  /** Sum of minutes × intensity (avg HR / max HR, or 0.6 without heart rate) */
  load: number;
}

/** One activity of a sport trend (insights) */
export interface TypeTrendPoint {
  t: string;
  speedKmh: number | null;
  avgHr: number | null;
  distanceKm: number | null;
}

/** How one sport type developed over the period (insights) */
export interface TypeTrend {
  type: string;
  /** One per activity, oldest first (at most 200) */
  points: TypeTrendPoint[];
  /** Average speed, last third vs first third of the period */
  speedChangePct: number | null;
  hrChangePct: number | null;
}

/** An MCP access token as listed */
export interface AccessTokenInfo {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  lastUsedAt?: string | null;
}

/** Activity types with their colour on the map */
export const ACTIVITY_TYPES: Record<string, { label: string; color: string }> = {
  ride: { label: 'Ride', color: '#3fb6ff' },
  hike: { label: 'Hike', color: '#5bd16b' },
  run: { label: 'Run', color: '#ff8a3d' },
  walk: { label: 'Walk', color: '#c9a7ff' },
  drive: { label: 'Drive', color: '#8b95a5' },
  transit: { label: 'Transit', color: '#6f7d92' },
  flight: { label: 'Flight', color: '#f2d15c' },
  ski: { label: 'Ski', color: '#9fe7ff' },
  swim: { label: 'Swim', color: '#3ee0c5' },
  other: { label: 'Other', color: '#b0b7c3' },
};

/** Colour of an activity type */
export const typeColor = (type: string): string =>
  ACTIVITY_TYPES[type]?.color ?? ACTIVITY_TYPES.other.color;

/** Place visits: a colour no activity type uses */
export const VISIT_COLOR = '#e879f9';
/** A matched route's inferred stretch (road with no GPS points): amber, readable on every map style */
export const INFERRED_COLOR = '#ffb020';
/** The dark casing under an inferred stretch, so the dashes stand out on light streets and imagery */
export const INFERRED_CASING = '#1f2937';
/** The active filters: time selection, area rectangle and the filter bar's chips */
export const FILTER_COLOR = '#3987e5';
/** Health data (heart rate) */
export const HEALTH_COLOR = '#ff4d6d';

/** Colour of a timeline/map kind: an activity type, 'visit' or 'health' */
export const kindColor = (kind: string): string =>
  kind === 'visit' ? VISIT_COLOR : kind === 'health' ? HEALTH_COLOR : typeColor(kind);

/** Name of a timeline/map kind */
export const kindLabel = (kind: string): string =>
  kind === 'visit'
    ? 'Place visits'
    : kind === 'health'
      ? 'Health data'
      : (ACTIVITY_TYPES[kind]?.label ?? kind);

/** Friendly names of the data sources */
export const SOURCE_LABELS: Record<string, string> = {
  'sports-tracker': 'Sports Tracker',
  'google-timeline': 'Google Timeline',
  'google-fit': 'Google Fit',
  'samsung-health': 'Samsung Health',
  fitbit: 'Fitbit',
  gpx: 'GPX file',
  tcx: 'TCX file',
  manual: 'Manual',
  user: 'You',
};

/**
 * Friendly name of a data source; unknown sources are shown as they are.
 * @param source - Source id, e.g. "sports-tracker"
 * @returns Display name, e.g. "Sports Tracker"
 */
export const sourceLabel = (source: string | null | undefined): string =>
  !source ? 'Unknown' : (SOURCE_LABELS[source] ?? source);

/** Friendly names of the fused fields (keys of ActivityDetail.fieldSources) */
export const FIELD_LABELS: Record<string, string> = {
  type: 'Type',
  name: 'Name',
  notes: 'Notes',
  startTime: 'Start',
  endTime: 'End',
  distanceM: 'Distance',
  durationS: 'Time',
  time: 'Times',
  elevationGainM: 'Climb',
  track: 'Track',
  heartRate: 'Heart rate',
  avgHeartRate: 'Heart rate',
  maxHeartRate: 'Max heart rate',
};

/**
 * Where a fused activity's values come from, as one line.
 * Fields with the same label (e.g. heartRate and avgHeartRate) are listed once.
 * @param fieldSources - Field → source
 * @returns e.g. "Distance: Sports Tracker · Heart rate: Fitbit"; '' when empty
 */
export const describeFieldSources = (fieldSources: Record<string, string> | null | undefined) => {
  const seen = new Set<string>();
  const parts: string[] = [];
  Object.entries(fieldSources ?? {}).forEach(([field, source]) => {
    const label = FIELD_LABELS[field] ?? field;
    if (seen.has(label)) {
      return;
    }
    seen.add(label);
    parts.push(`${label}: ${sourceLabel(source)}`);
  });
  return parts.join(' · ');
};

/** Error codes of the Sports Tracker connection (ApiRequestError.code) */
export const SPORTS_TRACKER_ERRORS = {
  /** Sports Tracker refused the server's sign-in: offer the session-key route */
  loginRefused: 'SPORTS_TRACKER_LOGIN_REFUSED',
  /** The pasted session key does not work */
  keyInvalid: 'SPORTS_TRACKER_KEY_INVALID',
  /** The stored session key has expired: sign in again */
  keyExpired: 'SPORTS_TRACKER_KEY_EXPIRED',
} as const;

/** Neither null nor undefined (a type guard) */
export const isSet = <T>(value: T | null | undefined): value is T =>
  value !== null && value !== undefined;
