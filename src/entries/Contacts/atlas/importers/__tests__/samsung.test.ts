import {
  SamsungCollector,
  parseSamsungCsv,
  samsungKind,
  samsungTime,
  samsungType,
} from '../samsung';
import { kindOf } from '..';

const EXERCISE_CSV = [
  'com.samsung.shealth.exercise,6312006,9',
  'com.samsung.health.exercise.start_time,com.samsung.health.exercise.end_time,com.samsung.health.exercise.exercise_type,com.samsung.health.exercise.distance,com.samsung.health.exercise.duration,com.samsung.health.exercise.mean_heart_rate,com.samsung.health.exercise.max_heart_rate,com.samsung.health.exercise.live_data,com.samsung.health.exercise.location_data,com.samsung.health.exercise.datauuid,com.samsung.health.exercise.title,',
  '2025-01-02 03:27:10.000,2025-01-02 04:59:40.000,11007,21000.5,4260000,134.0,175.0,ride-1.com.samsung.health.exercise.live_data.json,ride-1.com.samsung.health.exercise.location_data.json,ride-1,"Morning, cold",',
  '2025-01-03 06:00:00.000,2025-01-03 06:45:00.000,1002,0,2700000,150.0,171.0,run-2.com.samsung.health.exercise.live_data.json,,run-2,,',
].join('\n');

const t0 = Date.UTC(2025, 0, 2, 3, 27, 10);

describe('Samsung Health export', () => {
  it('recognizes the files of the export, also inside a zip', () => {
    expect(samsungKind('samsunghealth_x/com.samsung.shealth.exercise.20250201.csv')).toBe(
      'exercise',
    );
    expect(samsungKind('samsunghealth_x/com.samsung.shealth.tracker.heart_rate.20250201.csv')).toBe(
      'heartRate',
    );
    expect(
      samsungKind(
        'samsunghealth_x/jsons/com.samsung.shealth.exercise/r/ride-1.com.samsung.health.exercise.live_data.json',
      ),
    ).toBe('live');
    expect(
      samsungKind(
        'jsons/com.samsung.shealth.exercise/r/ride-1.com.samsung.health.exercise.location_data.json',
      ),
    ).toBe('location');
    expect(
      samsungKind(
        'jsons/com.samsung.shealth.tracker.heart_rate/a/x.com.samsung.health.heart_rate.binning_data.json',
      ),
    ).toBe('binning');
    expect(samsungKind('samsunghealth_x/com.samsung.shealth.sleep.20250201.csv')).toBeNull();
    expect(kindOf('samsunghealth_x/com.samsung.shealth.exercise.20250201.csv')).toBe('samsung');
  });

  it('reads the CSV with its table-name line, prefixed columns and quoted commas', () => {
    const rows = parseSamsungCsv(EXERCISE_CSV);
    expect(rows).toHaveLength(2);
    expect(rows[0].exercise_type).toBe('11007');
    expect(rows[0].datauuid).toBe('ride-1');
    expect(rows[0].title).toBe('Morning, cold');
    expect(samsungTime(rows[0].start_time)).toBe('2025-01-02T03:27:10.000Z');
    expect(samsungTime(t0)).toBe('2025-01-02T03:27:10.000Z');
    expect(samsungType(11007)).toBe('ride');
    expect(samsungType('1002')).toBe('run');
    expect(samsungType(9999)).toBe('other');
  });

  it('puts a workout together with its heart rate and GPS, in any file order', () => {
    const c = new SamsungCollector();
    const live = JSON.stringify(
      Array.from({ length: 60 }, (_, i) => ({
        start_time: t0 + i * 1000,
        heart_rate: 120 + i,
        speed: 7.5,
        cadence: 85,
      })),
    );
    const location = JSON.stringify(
      Array.from({ length: 12 }, (_, i) => ({
        start_time: t0 + i * 5000 + 400,
        latitude: 32 + i * 0.001,
        longitude: 34.8,
        altitude: 20,
      })),
    );
    // The JSON files first (as a zip may hold them), then the CSV
    expect(
      c.add(
        'jsons/com.samsung.shealth.exercise/r/ride-1.com.samsung.health.exercise.live_data.json',
        live,
      ).activities,
    ).toHaveLength(0);
    expect(
      c.add(
        'jsons/com.samsung.shealth.exercise/r/ride-1.com.samsung.health.exercise.location_data.json',
        location,
      ).activities,
    ).toHaveLength(0);
    const fromCsv = c.add('com.samsung.shealth.exercise.20250201.csv', EXERCISE_CSV);
    expect(fromCsv.source).toBe('samsung-health');
    const ride = fromCsv.activities.find((a) => a.externalId === 'ride-1')!;
    expect(ride.type).toBe('ride');
    expect(ride.durationS).toBe(4260);
    expect(ride.avgHeartRate).toBe(134);
    expect(ride.points).toHaveLength(12);
    expect(ride.points![3]).toMatchObject({ hr: 135, cad: 85 });

    // The run's live data comes later; it has no GPS: its heart rate goes in as health data
    const run = c.add(
      'jsons/com.samsung.shealth.exercise/u/run-2.com.samsung.health.exercise.live_data.json',
      JSON.stringify([
        { start_time: Date.UTC(2025, 0, 3, 6, 0, 0), heart_rate: 148 },
        { start_time: Date.UTC(2025, 0, 3, 6, 0, 1), heart_rate: 149 },
      ]),
    );
    expect(run.activities).toHaveLength(1);
    expect(run.activities[0].points).toBeUndefined();
    expect(run.health).toHaveLength(2);
    expect(c.finish().activities).toHaveLength(0);
  });

  it('reads all-day heart rate from the tracker CSV and the per-minute files', () => {
    const c = new SamsungCollector();
    const csv = c.add(
      'com.samsung.shealth.tracker.heart_rate.20250201.csv',
      'com.samsung.shealth.tracker.heart_rate,6312006,2\ncom.samsung.health.heart_rate.start_time,com.samsung.health.heart_rate.heart_rate\n2025-01-05 10:00:00.000,61.0\n',
    );
    expect(csv.health).toEqual([
      { type: 'heart_rate', startTime: '2025-01-05T10:00:00.000Z', value: 61, unit: 'bpm' },
    ]);
    const bins = c.add(
      'jsons/com.samsung.shealth.tracker.heart_rate/a/x.com.samsung.health.heart_rate.binning_data.json',
      JSON.stringify([
        { start_time: Date.UTC(2025, 0, 5, 10, 1), heart_rate: 64.0 },
        { start_time: Date.UTC(2025, 0, 5, 10, 2), heart_rate: 0 },
      ]),
    );
    expect(bins.health).toHaveLength(1);
  });
});
