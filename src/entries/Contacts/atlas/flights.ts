/**
 * @fileoverview A flight's identity and public details (AD-42) as the API returns them,
 * and how they are shown: local times, the path drawn on the map
 * @module entries/Contacts/atlas/flights
 */
import { greatCircle } from './geo';

/** An airport as shown with a flight */
export interface FlightAirport {
  icao: string;
  iata: string;
  name: string;
  city: string;
  country: string;
  tz: string;
  lat: number;
  lon: number;
}

/** An airline */
export interface FlightAirline {
  name: string;
  iata?: string | null;
  icao?: string | null;
}

/** A public flight that fits */
export interface FlightCandidate {
  callsign: string;
  icao24: string;
  firstSeen: string;
  lastSeen: string;
  departure?: string | null;
  arrival?: string | null;
  airline?: string | null;
  flightNumber?: string | null;
  score: number;
}

/** Everything known of a flight beyond its identity (AD-42 rule 9) */
export interface FlightDetails {
  departure?: FlightAirport | null;
  arrival?: FlightAirport | null;
  departureCandidates: string[];
  arrivalCandidates: string[];
  scheduledDeparture?: string | null;
  scheduledArrival?: string | null;
  takeoff?: string | null;
  landing?: string | null;
  mail?: { id: string; subject: string; from: string; date: string } | null;
  airline?: FlightAirline | null;
  operatedBy?: FlightAirline | null;
  callsign?: string | null;
  icao24?: string | null;
  /** callsign, route or confirmed */
  match?: string | null;
  aircraft?: {
    type?: string | null;
    icaoType?: string | null;
    manufacturer?: string | null;
    registration?: string | null;
    owner?: string | null;
  } | null;
  /** [unix seconds, lat, lon, altitude m] */
  track?: number[][] | null;
  trackNote?: string | null;
  candidates: FlightCandidate[];
  checked: { openSky?: string | null; adsbdb?: string | null };
}

/** pending, found, mail_only, suggested, not_found, not_a_flight, no_airports, error */
export type FlightStatus =
  | 'pending'
  | 'found'
  | 'mail_only'
  | 'suggested'
  | 'not_found'
  | 'not_a_flight'
  | 'no_airports'
  | 'error';

/** A flight as the API returns it */
export interface Flight {
  activityId: string;
  status: FlightStatus;
  note?: string | null;
  flightNumber?: string | null;
  /** yyyy-mm-dd, the departure's local date */
  flightDate?: string | null;
  /** user, confirmed, mail */
  source?: string | null;
  /** searched, not_allowed, sign_in, failed */
  mailState?: string | null;
  details: FlightDetails;
  checkedAt?: string | null;
  /** Whether public flight data is set up */
  publicData: boolean;
}

/** A time in an airport's local time ("08:50"), with the zone's short name when asked */
export const localTime = (iso: string, tz: string | undefined, withZone = false): string => {
  const opts: Intl.DateTimeFormatOptions = {
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
    timeZone: tz || 'UTC',
    ...(withZone ? { timeZoneName: 'short' } : {}),
  };
  try {
    return new Intl.DateTimeFormat('en-GB', opts).format(new Date(iso));
  } catch {
    return new Intl.DateTimeFormat('en-GB', { ...opts, timeZone: 'UTC' }).format(new Date(iso));
  }
};

/** A day in an airport's local time ("27 Aug 2026") */
export const localDay = (iso: string, tz: string | undefined): string => {
  const opts: Intl.DateTimeFormatOptions = {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    timeZone: tz || 'UTC',
  };
  try {
    return new Intl.DateTimeFormat('en-GB', opts).format(new Date(iso));
  } catch {
    return new Intl.DateTimeFormat('en-GB', { ...opts, timeZone: 'UTC' }).format(new Date(iso));
  }
};

/** "2 h 19 min" */
export const minutesText = (minutes: number): string => {
  const m = Math.round(Math.abs(minutes));
  return m >= 60 ? `${Math.floor(m / 60)} h ${m % 60} min` : `${m} min`;
};

/** How the take-off compares with the schedule ("took off 22 min after the scheduled departure") */
export const takeoffText = (scheduled: string, takeoff: string): string => {
  const d = (Date.parse(takeoff) - Date.parse(scheduled)) / 60000;
  if (Math.abs(d) < 1) {
    return 'took off at the scheduled departure';
  }
  return `took off ${minutesText(d)} ${d > 0 ? 'after' : 'before'} the scheduled departure`;
};

/** Great-circle distance between two airports (km) */
export const airportKm = (a: FlightAirport, b: FlightAirport): number => {
  const r = Math.PI / 180;
  const h =
    Math.sin(((b.lat - a.lat) * r) / 2) ** 2 +
    Math.cos(a.lat * r) * Math.cos(b.lat * r) * Math.sin(((b.lon - a.lon) * r) / 2) ** 2;
  return 12742 * Math.asin(Math.min(1, Math.sqrt(h)));
};

/**
 * The selected flight's path on the map (AD-42 rule 8): the flown track, else the great
 * circle between the airports, else null (the map draws its own arc)
 */
export const flightPath = (flight: Flight | null | undefined): [number, number][] | null => {
  const d = flight?.details;
  if (!d) {
    return null;
  }
  if (d.track && d.track.length > 1) {
    return d.track.map((p) => [p[1], p[2]] as [number, number]);
  }
  if (d.departure && d.arrival && d.departure.iata !== d.arrival.iata) {
    return greatCircle([d.departure.lat, d.departure.lon], [d.arrival.lat, d.arrival.lon]);
  }
  return null;
};

/** The link that opens a mail in Gmail */
export const gmailLink = (id: string): string =>
  `https://mail.google.com/mail/#all/${encodeURIComponent(id)}`;
