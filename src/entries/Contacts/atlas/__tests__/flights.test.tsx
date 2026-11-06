import React from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { axe } from 'jest-axe';
import FlightSection from '../FlightSection';
import {
  airportKm,
  flightPath,
  gmailLink,
  localTime,
  minutesText,
  takeoffText,
  type Flight,
  type FlightAirport,
} from '../flights';

jest.mock('../../../../services/api/BackendApiClient', () => ({
  backendApiClient: {
    atlasFlight: jest.fn(),
    atlasSetFlightNumber: jest.fn(),
    atlasClearFlightNumber: jest.fn(),
    atlasSearchFlight: jest.fn(),
    atlasConfirmFlight: jest.fn(),
    connectMailForFlights: jest.fn(),
  },
}));

// eslint-disable-next-line @typescript-eslint/no-var-requires
const api = jest.requireMock('../../../../services/api/BackendApiClient');
const client = api.backendApiClient as Record<string, jest.Mock>;

const AUH: FlightAirport = {
  icao: 'OMAA',
  iata: 'AUH',
  name: 'Abu Dhabi International Airport',
  city: 'Abu Dhabi',
  country: 'AE',
  tz: 'Asia/Dubai',
  lat: 24.433,
  lon: 54.6511,
};
const TLV: FlightAirport = {
  icao: 'LLBG',
  iata: 'TLV',
  name: 'Ben Gurion International Airport',
  city: 'Tel Aviv',
  country: 'IL',
  tz: 'Asia/Jerusalem',
  lat: 32.0114,
  lon: 34.8867,
};

const flight = (f: Partial<Flight> = {}, d: Partial<Flight['details']> = {}): Flight => ({
  activityId: 'a1',
  status: 'found',
  note: null,
  flightNumber: 'EY593',
  flightDate: '2026-03-17',
  source: 'mail',
  mailState: 'searched',
  checkedAt: '2026-03-20T10:00:00Z',
  publicData: true,
  ...f,
  details: {
    departure: AUH,
    arrival: TLV,
    departureCandidates: ['AUH'],
    arrivalCandidates: ['TLV'],
    scheduledDeparture: '2026-03-17T04:50:00Z',
    scheduledArrival: '2026-03-17T09:10:00Z',
    takeoff: '2026-03-17T05:12:00Z',
    landing: '2026-03-17T07:31:00Z',
    mail: {
      id: 'm1',
      subject: 'Your booking confirmation',
      from: 'a@b.example',
      date: '2026-01-10T00:00:00Z',
    },
    airline: { name: 'Etihad Airways', iata: 'EY', icao: 'ETD' },
    callsign: 'ETD5SM',
    icao24: '896123',
    match: 'callsign',
    aircraft: { type: 'A321neo', manufacturer: 'Airbus', registration: 'A6-AEA' },
    track: null,
    trackNote:
      'The flown path is kept publicly for 30 days only; the map shows the great-circle route between the airports.',
    candidates: [],
    checked: { openSky: '2026-03-20T10:00:00Z', adsbdb: '2026-03-20T10:00:00Z' },
    ...d,
  },
});

describe('flight text', () => {
  it("shows times in each airport's local time", () => {
    expect(localTime('2026-03-17T04:50:00Z', 'Asia/Dubai')).toBe('08:50');
    expect(localTime('2026-03-17T09:10:00Z', 'Asia/Jerusalem')).toBe('11:10');
    expect(localTime('2026-03-17T09:10:00Z', 'Not/AZone')).toBe('09:10');
    expect(minutesText(139)).toBe('2 h 19 min');
    expect(takeoffText('2026-03-17T04:50:00Z', '2026-03-17T05:12:00Z')).toBe(
      'took off 22 min after the scheduled departure',
    );
    expect(Math.round(airportKm(AUH, TLV))).toBeGreaterThan(1700);
    expect(gmailLink('a/b')).toBe('https://mail.google.com/mail/#all/a%2Fb');
  });

  it('draws the flown track, else the great circle between the airports', () => {
    expect(
      flightPath(
        flight(
          {},
          {
            track: [
              [0, 24.4, 54.6, 0],
              [1, 32, 34.9, 0],
            ],
          },
        ),
      ),
    ).toEqual([
      [24.4, 54.6],
      [32, 34.9],
    ]);
    const arc = flightPath(flight())!;
    expect(arc[0]).toEqual([AUH.lat, AUH.lon]);
    expect(arc[arc.length - 1][0]).toBeCloseTo(TLV.lat, 3);
    expect(flightPath(flight({}, { departure: null }))).toBeNull();
    expect(flightPath(null)).toBeNull();
  });
});

describe('FlightSection', () => {
  beforeEach(() => jest.resetAllMocks());

  it('shows the flight, where its number came from and its actual times', async () => {
    client.atlasFlight.mockResolvedValue(flight());
    const onPath = jest.fn();
    const { container } = render(<FlightSection activityId="a1" onPath={onPath} />);

    expect(await screen.findByText('EY593 · Etihad Airways')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Your booking confirmation' })).toHaveAttribute(
      'href',
      'https://mail.google.com/mail/#all/m1',
    );
    expect(screen.getByText('AUH Abu Dhabi → TLV Tel Aviv')).toBeInTheDocument();
    expect(
      screen.getByText(/took off 22 min after the scheduled departure · 2 h 19 min in the air/),
    ).toBeInTheDocument();
    expect(screen.getByText('Airbus A321neo · A6-AEA')).toBeInTheDocument();
    expect(screen.getByText(/kept publicly for 30 days only/)).toBeInTheDocument();
    expect(onPath).toHaveBeenLastCalledWith(expect.arrayContaining([[AUH.lat, AUH.lon]]));
    expect(await axe(container)).toHaveNoViolations();
  }, 20000);

  it('takes a typed flight number and offers suggestions to confirm', async () => {
    client.atlasFlight.mockResolvedValue(
      flight(
        { status: 'suggested', flightNumber: null, source: null, mailState: 'not_allowed' },
        {
          mail: null,
          scheduledDeparture: null,
          candidates: [
            {
              callsign: 'ETD5SM',
              icao24: '896123',
              firstSeen: '2026-03-17T05:12:00Z',
              lastSeen: '2026-03-17T07:31:00Z',
              departure: 'OMAA',
              arrival: 'LLBG',
              airline: 'Etihad Airways',
              flightNumber: 'EY593',
              score: 2,
            },
          ],
        },
      ),
    );
    // Confirmed, the flight is looked up again; here the answer still shows the choice
    client.atlasConfirmFlight.mockImplementation(() => client.atlasFlight());
    client.atlasSetFlightNumber.mockResolvedValue(flight({ source: 'user', status: 'pending' }));
    render(<FlightSection activityId="a1" />);

    expect(await screen.findByText('Which flight was it?')).toBeInTheDocument();
    expect(screen.getByText('Etihad Airways · EY593')).toBeInTheDocument();
    expect(screen.getByText('took off 09:12 · landed 09:31')).toBeInTheDocument();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'This one' }));
    });
    expect(client.atlasConfirmFlight).toHaveBeenCalledWith('a1', '896123', '2026-03-17T05:12:00Z');
    fireEvent.click(screen.getByRole('button', { name: 'Allow reading email content' }));
    expect(client.connectMailForFlights).toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText('Flight number'), { target: { value: 'ey 593' } });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Look up' }));
    });
    expect(client.atlasSetFlightNumber).toHaveBeenCalledWith('a1', 'ey 593');
  });

  it('says it is looking the flight up, and what went wrong', async () => {
    client.atlasFlight.mockResolvedValue(
      flight({ status: 'pending', flightNumber: null, source: null }),
    );
    render(<FlightSection activityId="a1" />);
    expect(await screen.findByRole('status')).toHaveTextContent('Looking up the flight…');

    client.atlasFlight.mockRejectedValue(new Error('Loading the flight failed'));
    render(<FlightSection activityId="a2" />);
    expect(await screen.findByText('Loading the flight failed')).toBeInTheDocument();
  });
});
