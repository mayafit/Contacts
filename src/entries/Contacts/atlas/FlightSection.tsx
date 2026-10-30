/**
 * @fileoverview The flight block of a flight's activity panel (AD-42, FR-72 to FR-76):
 * which flight it was and where that came from, the airports, scheduled and actual times,
 * the aircraft, the path, suggestions to confirm and the flight number to type
 * @module Contacts/atlas/FlightSection
 */

import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Box, Button, Link, TextField, Typography } from '@mui/material';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import PanelSection from './PanelSection';
import { tokens } from '../theme/contactsTheme';
import {
  airportKm,
  flightPath,
  gmailLink,
  localDay,
  localTime,
  minutesText,
  takeoffText,
  type Flight,
  type FlightAirport,
} from './flights';

/** How often the panel asks while the flight is looked up, and for how long */
const POLL_MS = 4000;
const POLL_FOR_MS = 120000;

export interface FlightSectionProps {
  activityId: string;
  /** The path to draw for the selected flight (track or great circle; null: the map's own) */
  onPath?: (path: [number, number][] | null) => void;
}

/** "AUH Abu Dhabi" */
const airportText = (a: FlightAirport) => `${a.iata} ${a.city || a.name}`;

/** A label and its value, on one line */
const Fact: React.FC<{ label: string; children: React.ReactNode }> = ({ label, children }) => (
  <Box sx={{ display: 'flex', gap: 1, alignItems: 'baseline' }}>
    <Typography variant="caption" sx={{ color: tokens.textMuted, minWidth: 92, flexShrink: 0 }}>
      {label}
    </Typography>
    <Typography variant="body2" component="div" sx={{ minWidth: 0 }}>
      {children}
    </Typography>
  </Box>
);

/** The status line of a flight not (fully) found */
const statusText = (f: Flight): string | null => {
  switch (f.status) {
    case 'pending':
      return 'Looking up the flight…';
    case 'not_a_flight':
    case 'no_airports':
    case 'error':
    case 'mail_only':
      return f.note ?? null;
    case 'not_found':
      return f.note ?? 'No flight found.';
    default:
      return f.note ?? null;
  }
};

/**
 * The flight block.
 * @param props - Component props
 * @returns The block
 */
const FlightSection: React.FC<FlightSectionProps> = ({ activityId, onPath }) => {
  const [flight, setFlight] = useState<Flight | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState(false);
  const [number, setNumber] = useState('');
  const pollUntil = useRef(0);
  // An answer that arrives after the panel closed changes nothing (not the map's path)
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  const onPathRef = useRef(onPath);
  onPathRef.current = onPath;

  const show = useCallback((f: Flight) => {
    if (!mounted.current) {
      return;
    }
    setFlight(f);
    onPathRef.current?.(flightPath(f));
  }, []);

  // A change by the Owner starts polling afresh
  const [pollKey, setPollKey] = useState(0);

  // Load, then poll while it is looked up (2 minutes at most)
  useEffect(() => {
    let alive = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    pollUntil.current = Date.now() + POLL_FOR_MS;
    const load = async () => {
      try {
        const f = await backendApiClient.atlasFlight(activityId);
        if (!alive) {
          return;
        }
        show(f);
        setError(null);
        if (f.status === 'pending' && Date.now() < pollUntil.current) {
          timer = setTimeout(() => void load(), POLL_MS);
        }
      } catch (e) {
        if (alive) {
          setError((e as Error).message);
        }
      }
    };
    // After a change the answer is already shown: the first ask waits
    if (pollKey === 0) {
      void load();
    } else {
      timer = setTimeout(() => void load(), POLL_MS);
    }
    return () => {
      alive = false;
      if (timer) {
        clearTimeout(timer);
      }
    };
  }, [activityId, pollKey, show]);

  // The map's path goes with the panel
  useEffect(() => () => onPathRef.current?.(null), [activityId]);

  const act = async (call: () => Promise<Flight>) => {
    setBusy(true);
    setError(null);
    try {
      const f = await call();
      if (!mounted.current) {
        return;
      }
      show(f);
      setEditing(false);
      setPollKey((k) => k + 1);
    } catch (e) {
      if (mounted.current) {
        setError((e as Error).message);
      }
    } finally {
      if (mounted.current) {
        setBusy(false);
      }
    }
  };

  if (!flight) {
    return error ? (
      <PanelSection title="Flight">
        <Typography variant="caption" color="error">
          {error}
        </Typography>
      </PanelSection>
    ) : null;
  }

  const d = flight.details;
  const dep = d.departure ?? null;
  const arr = d.arrival ?? null;
  const ident = flight.flightNumber ?? (flight.source === 'confirmed' ? d.callsign : null);
  const status = statusText(flight);
  const showInput = editing || (!ident && flight.status !== 'pending');
  const sourceLine =
    flight.source === 'mail' && d.mail ? (
      <>
        from your mail:{' '}
        <Link
          href={gmailLink(d.mail.id)}
          target="_blank"
          rel="noopener noreferrer"
          underline="hover"
        >
          {d.mail.subject || '(no subject)'}
        </Link>{' '}
        ({localDay(d.mail.date, dep?.tz)})
      </>
    ) : flight.source === 'user' ? (
      'you entered it'
    ) : flight.source === 'confirmed' ? (
      'you confirmed it'
    ) : null;

  return (
    <PanelSection title="Flight" hint={ident ?? undefined}>
      {ident && (
        <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1, flexWrap: 'wrap' }}>
          <Typography variant="subtitle2" component="span">
            {ident}
            {d.airline ? ` · ${d.airline.name}` : ''}
          </Typography>
          {!editing && (
            <Link component="button" variant="caption" onClick={() => setEditing(true)}>
              Change
            </Link>
          )}
          {(flight.source === 'user' || flight.source === 'confirmed') && !editing && (
            <Link
              component="button"
              variant="caption"
              disabled={busy}
              onClick={() => void act(() => backendApiClient.atlasClearFlightNumber(activityId))}
            >
              Clear
            </Link>
          )}
        </Box>
      )}
      {sourceLine && (
        <Typography variant="caption" component="div" sx={{ color: tokens.textSecondary }}>
          {sourceLine}
        </Typography>
      )}

      {dep && arr && (
        <Typography variant="body2" sx={{ mt: 0.5 }}>
          {airportText(dep)} → {airportText(arr)}
        </Typography>
      )}

      {(d.scheduledDeparture || d.takeoff || d.aircraft || d.callsign) &&
        flight.status !== 'suggested' && (
          <Box sx={{ mt: 0.75, display: 'grid', gap: 0.25 }}>
            {d.scheduledDeparture && (
              <Fact label="Scheduled">
                {localTime(d.scheduledDeparture, dep?.tz, true)}
                {d.scheduledArrival ? ` → ${localTime(d.scheduledArrival, arr?.tz, true)}` : ''}
              </Fact>
            )}
            {d.takeoff && d.landing && flight.status === 'found' && (
              <Fact label="Took off · landed">
                {localTime(d.takeoff, dep?.tz, true)} → {localTime(d.landing, arr?.tz, true)}
                <Typography variant="caption" component="div" sx={{ color: tokens.textMuted }}>
                  {d.scheduledDeparture ? `${takeoffText(d.scheduledDeparture, d.takeoff)} · ` : ''}
                  {minutesText((Date.parse(d.landing) - Date.parse(d.takeoff)) / 60000)} in the air
                </Typography>
              </Fact>
            )}
            {d.aircraft && (d.aircraft.type || d.aircraft.registration) && (
              <Fact label="Aircraft">
                {[
                  [d.aircraft.manufacturer, d.aircraft.type].filter(Boolean).join(' '),
                  d.aircraft.registration,
                ]
                  .filter(Boolean)
                  .join(' · ')}
              </Fact>
            )}
            {d.callsign && flight.status === 'found' && (
              <Fact label="Call sign">
                {d.callsign}
                {d.match === 'route' ? ' · matched by route and time' : ''}
              </Fact>
            )}
            {d.operatedBy && <Fact label="Operated by">{d.operatedBy.name}</Fact>}
            {dep && arr && dep.iata !== arr.iata && (
              <Fact label="Distance">
                {Math.round(airportKm(dep, arr)).toLocaleString('en-US')} km
              </Fact>
            )}
          </Box>
        )}

      {flight.status === 'found' && (
        <Typography variant="caption" component="div" sx={{ color: tokens.textMuted, mt: 0.5 }}>
          {d.track && d.track.length > 1 ? 'Flown path from ADS-B.' : d.trackNote}
        </Typography>
      )}

      {flight.status === 'suggested' ||
      (flight.status === 'mail_only' && d.candidates.length > 0) ? (
        <Box sx={{ mt: 0.75 }}>
          <Typography variant="body2">Which flight was it?</Typography>
          {d.candidates.map((c) => (
            <Box
              key={`${c.icao24}-${c.firstSeen}`}
              sx={{ display: 'flex', alignItems: 'center', gap: 1, mt: 0.5 }}
            >
              <Box sx={{ flex: 1, minWidth: 0 }}>
                <Typography variant="body2" noWrap>
                  {[c.airline, c.flightNumber ?? c.callsign].filter(Boolean).join(' · ')}
                </Typography>
                <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                  took off {localTime(c.firstSeen, dep?.tz)} · landed{' '}
                  {localTime(c.lastSeen, arr?.tz)}
                </Typography>
              </Box>
              <Button
                size="small"
                variant="outlined"
                disabled={busy}
                onClick={() =>
                  void act(() =>
                    backendApiClient.atlasConfirmFlight(activityId, c.icao24, c.firstSeen),
                  )
                }
              >
                This one
              </Button>
            </Box>
          ))}
        </Box>
      ) : null}

      {status && (
        <Typography
          variant="caption"
          component="div"
          role={flight.status === 'pending' ? 'status' : undefined}
          sx={{
            color: flight.status === 'error' ? tokens.attention : tokens.textSecondary,
            mt: 0.5,
          }}
        >
          {status}
        </Typography>
      )}

      {flight.status !== 'pending' && !flight.source && flight.mailState === 'not_allowed' && (
        <Typography variant="caption" component="div" sx={{ color: tokens.textSecondary, mt: 0.5 }}>
          Mail not searched: Echo may not read email content.{' '}
          <Link
            component="button"
            variant="caption"
            onClick={() => backendApiClient.connectMailForFlights()}
          >
            Allow reading email content
          </Link>
        </Typography>
      )}
      {flight.status !== 'pending' && !flight.source && flight.mailState === 'sign_in' && (
        <Typography variant="caption" component="div" sx={{ color: tokens.textSecondary, mt: 0.5 }}>
          Sign in to Google again to search your mail.
        </Typography>
      )}

      {showInput && (
        <Box
          component="form"
          sx={{ display: 'flex', gap: 1, mt: 1, flexWrap: 'wrap', alignItems: 'flex-start' }}
          onSubmit={(e) => {
            e.preventDefault();
            void act(() => backendApiClient.atlasSetFlightNumber(activityId, number));
          }}
        >
          <TextField
            size="small"
            label="Flight number"
            placeholder="e.g. LY315"
            value={number}
            onChange={(e) => setNumber(e.target.value)}
            sx={{ width: 140 }}
            inputProps={{ 'aria-label': 'Flight number', maxLength: 10 }}
          />
          <Button type="submit" size="small" variant="contained" disabled={busy || !number.trim()}>
            Look up
          </Button>
          {editing && (
            <Button size="small" onClick={() => setEditing(false)}>
              Cancel
            </Button>
          )}
        </Box>
      )}

      {flight.status !== 'pending' && (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mt: 0.75, flexWrap: 'wrap' }}>
          <Link
            component="button"
            variant="caption"
            disabled={busy}
            onClick={() => void act(() => backendApiClient.atlasSearchFlight(activityId))}
          >
            Search again
          </Link>
          {(d.checked.openSky || d.checked.adsbdb) && (
            <Typography variant="caption" sx={{ color: tokens.textMuted }}>
              Flight data: The OpenSky Network · Airlines and aircraft: adsbdb · read{' '}
              {localDay((d.checked.openSky ?? d.checked.adsbdb)!, undefined)}
            </Typography>
          )}
        </Box>
      )}

      {error && (
        <Typography variant="caption" color="error" component="div" sx={{ mt: 0.5 }}>
          {error}
        </Typography>
      )}
    </PanelSection>
  );
};

export default FlightSection;
