import { parseGpx, parseTcx, typeFromSport } from '../tracks';
import {
  parseFitbitHeartRate,
  parseGoogleFit,
  parseLatLng,
  parseTimelineDevice,
  parseTimelineTakeout,
} from '../google';
import { kindOf, parseFile } from '..';

const GPX = `<?xml version="1.0"?>
<gpx version="1.1" creator="Sports Tracker" xmlns="http://www.topografix.com/GPX/1/1"
     xmlns:gpxtpx="http://www.garmin.com/xmlschemas/TrackPointExtension/v1">
  <trk><name>Carmel loop</name><type>Mountain biking</type><trkseg>
    <trkpt lat="32.75" lon="35.02"><ele>120</ele><time>2026-09-03T06:00:00Z</time>
      <extensions><gpxtpx:TrackPointExtension><gpxtpx:hr>121</gpxtpx:hr><gpxtpx:cad>80</gpxtpx:cad></gpxtpx:TrackPointExtension></extensions></trkpt>
    <trkpt lat="32.751" lon="35.021"><ele>125</ele><time>2026-09-03T06:00:10Z</time>
      <extensions><gpxtpx:TrackPointExtension><gpxtpx:hr>130</gpxtpx:hr></gpxtpx:TrackPointExtension></extensions></trkpt>
  </trkseg></trk>
</gpx>`;

const TCX = `<?xml version="1.0"?>
<TrainingCenterDatabase xmlns="http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2">
  <Activities><Activity Sport="Running"><Id>2026-09-04T05:00:00Z</Id><Lap><Track>
    <Trackpoint><Time>2026-09-04T05:00:00Z</Time><Position><LatitudeDegrees>32.08</LatitudeDegrees><LongitudeDegrees>34.78</LongitudeDegrees></Position><AltitudeMeters>10</AltitudeMeters><DistanceMeters>0</DistanceMeters><HeartRateBpm><Value>140</Value></HeartRateBpm></Trackpoint>
    <Trackpoint><Time>2026-09-04T05:00:05Z</Time><Position><LatitudeDegrees>32.0805</LatitudeDegrees><LongitudeDegrees>34.78</LongitudeDegrees></Position><DistanceMeters>55.5</DistanceMeters><HeartRateBpm><Value>145</Value></HeartRateBpm></Trackpoint>
  </Track></Lap></Activity></Activities>
</TrainingCenterDatabase>`;

describe('track files', () => {
  it('reads a Sports Tracker GPX with heart rate and cadence', () => {
    const r = parseGpx(GPX, 'ride.gpx', 'sports-tracker');
    expect(r.source).toBe('sports-tracker');
    const a = r.activities[0];
    expect(a.type).toBe('ride');
    expect(a.name).toBe('Carmel loop');
    expect(a.points).toHaveLength(2);
    expect(a.points![0]).toMatchObject({ lat: 32.75, lon: 35.02, ele: 120, hr: 121, cad: 80 });
    expect(a.startTime).toBe('2026-09-03T06:00:00.000Z');
  });

  it('reads a TCX run', () => {
    const a = parseTcx(TCX, 'run.tcx').activities[0];
    expect(a.type).toBe('run');
    expect(a.distanceM).toBe(55.5);
    expect(a.points!.map((p) => p.hr)).toEqual([140, 145]);
  });

  it('maps sport names to types', () => {
    expect(typeFromSport('Cycling')).toBe('ride');
    expect(typeFromSport('Hiking')).toBe('hike');
    expect(typeFromSport('Mountain hiking')).toBe('hike');
    expect(typeFromSport('Nordic walking')).toBe('walk');
  });
});

describe('Google exports', () => {
  it('parses coordinates in every Timeline form', () => {
    expect(parseLatLng('32.0853°, 34.7818°')).toEqual({ lat: 32.0853, lon: 34.7818 });
    expect(parseLatLng('geo:32.0853,34.7818')).toEqual({ lat: 32.0853, lon: 34.7818 });
    expect(parseLatLng({ latitudeE7: 320853000, longitudeE7: 347818000 })).toEqual({
      lat: 32.0853,
      lon: 34.7818,
    });
  });

  it('reads the on-device Timeline export', () => {
    const r = parseTimelineDevice({
      semanticSegments: [
        {
          startTime: '2026-09-03T08:00:00.000+03:00',
          endTime: '2026-09-03T09:00:00.000+03:00',
          visit: {
            probability: 0.9,
            topCandidate: {
              placeId: 'ChIJ1',
              semanticType: 'HOME',
              probability: 0.8,
              placeLocation: { latLng: '32.1°, 34.8°' },
            },
          },
        },
        {
          startTime: '2026-09-03T09:00:00.000+03:00',
          endTime: '2026-09-03T09:30:00.000+03:00',
          activity: {
            start: { latLng: '32.1°, 34.8°' },
            end: { latLng: '32.2°, 34.9°' },
            distanceMeters: 15000,
            topCandidate: { type: 'IN_PASSENGER_VEHICLE' },
          },
        },
        {
          startTime: '2026-09-03T09:00:00.000+03:00',
          endTime: '2026-09-03T11:00:00.000+03:00',
          timelinePath: [{ point: '32.15°, 34.85°', time: '2026-09-03T09:15:00.000+03:00' }],
        },
      ],
    });
    expect(r.places).toEqual([
      expect.objectContaining({ externalId: 'ChIJ1', name: 'Home', category: 'home' }),
    ]);
    expect(r.visits[0]).toMatchObject({
      placeExternalId: 'ChIJ1',
      startTime: '2026-09-03T05:00:00.000Z',
    });
    expect(r.activities[0].type).toBe('IN_PASSENGER_VEHICLE');
    expect(r.activities[0].points).toHaveLength(3);
  });

  it('reads older Takeout Semantic Location History', () => {
    const r = parseTimelineTakeout({
      timelineObjects: [
        {
          placeVisit: {
            location: {
              latitudeE7: 327940000,
              longitudeE7: 349890000,
              placeId: 'ChIJh',
              name: 'Haifa café',
              address: 'Haifa',
            },
            duration: {
              startTimestamp: '2021-05-01T08:00:00Z',
              endTimestamp: '2021-05-01T09:00:00Z',
            },
            visitConfidence: 90,
          },
        },
        {
          activitySegment: {
            startLocation: { latitudeE7: 327940000, longitudeE7: 349890000 },
            endLocation: { latitudeE7: 328000000, longitudeE7: 350000000 },
            duration: {
              startTimestamp: '2021-05-01T09:00:00Z',
              endTimestamp: '2021-05-01T10:00:00Z',
            },
            activityType: 'CYCLING',
            distance: 12000,
            waypointPath: { waypoints: [{ latE7: 327970000, lngE7: 349950000 }] },
          },
        },
      ],
    });
    expect(r.places[0].name).toBe('Haifa café');
    expect(r.visits[0].confidence).toBe(0.9);
    expect(r.activities[0].points).toHaveLength(3);
    expect(r.activities[0].points![1].t).toBe('2021-05-01T09:30:00.000Z');
  });

  it('reads Google Fit and Fitbit heart rate', () => {
    const fit = parseGoogleFit({
      'Data Source': 'derived:com.google.heart_rate.bpm',
      'Data Points': [
        {
          dataTypeName: 'com.google.heart_rate.bpm',
          startTimeNanos: '1756886400000000000',
          endTimeNanos: '1756886400000000000',
          fitValue: [{ value: { fpVal: 72 } }],
        },
        {
          dataTypeName: 'com.google.unknown',
          startTimeNanos: '1',
          fitValue: [{ value: { fpVal: 1 } }],
        },
      ],
    });
    expect(fit.health).toEqual([
      {
        type: 'heart_rate',
        startTime: '2025-09-03T08:00:00.000Z',
        endTime: null,
        value: 72,
        unit: 'bpm',
      },
    ]);
    const fitbit = parseFitbitHeartRate(
      [{ dateTime: '09/03/26 06:00:00', value: { bpm: 66, confidence: 2 } }],
      180,
    );
    expect(fitbit.health[0]).toMatchObject({ startTime: '2026-09-03T03:00:00.000Z', value: 66 });
  });

  it('recognizes files by path and content', async () => {
    expect(kindOf('Takeout/Fit/All Data/derived_com.google.heart_rate.bpm.json')).toBe('json');
    expect(kindOf('Takeout/Mail/All mail.mbox')).toBeNull();
    expect(kindOf('Takeout/Location History (Timeline)/Records.json')).toBeNull();
    expect(kindOf('Takeout/Maps/Saved/places.json')).toBeNull();
    const r = await parseFile({
      path: 'Timeline.json',
      text: async () => JSON.stringify({ semanticSegments: [] }),
    });
    expect(r?.source).toBe('google-timeline');
    expect(await parseFile({ path: 'other.json', text: async () => '{"x":1}' })).toBeNull();
  });

  it('reads epoch-millisecond strings from older Takeout', () => {
    const old = parseTimelineTakeout({
      timelineObjects: [
        {
          placeVisit: {
            location: { latitudeE7: 320853000, longitudeE7: 347818000, name: 'Café' },
            duration: { startTimestampMs: '1619856000000', endTimestampMs: '1619859600000' },
          },
        },
      ],
    });
    expect(old.visits[0]).toMatchObject({
      startTime: '2021-05-01T08:00:00.000Z',
      endTime: '2021-05-01T09:00:00.000Z',
    });
  });
});
