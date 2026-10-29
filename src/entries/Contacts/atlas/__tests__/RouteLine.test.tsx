import React from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { axe } from 'jest-axe';
import RouteLine from '../RouteLine';
import type { ActivityDetail, ActivityRouteInfo, ActivitySummary } from '../types';

jest.mock('../../../../services/api/BackendApiClient', () => {
  class ApiRequestError extends Error {
    code: string;
    status: number;
    constructor(message: string, status: number, code: string) {
      super(message);
      this.code = code;
      this.status = status;
    }
  }
  return {
    ApiRequestError,
    backendApiClient: {
      routesDecide: jest.fn(),
      routesClearDecision: jest.fn(),
      routesImprove: jest.fn(),
      routesJob: jest.fn(),
    },
  };
});

// eslint-disable-next-line @typescript-eslint/no-var-requires
const api = jest.requireMock('../../../../services/api/BackendApiClient');
const client = api.backendApiClient as Record<string, jest.Mock>;

const summary = (s: Partial<ActivitySummary> = {}): ActivitySummary => ({
  id: 'a1',
  type: 'drive',
  startTime: '2026-09-21T06:00:00Z',
  endTime: '2026-09-21T06:30:00Z',
  source: 'google-timeline',
  pointCount: 12,
  sources: ['google-timeline'],
  memberCount: 1,
  conflictCount: 0,
  geometry: 'recorded',
  ...s,
});

const route = (
  r: Partial<NonNullable<ActivityRouteInfo['description']>> = {},
): ActivityRouteInfo => ({
  recordIds: ['a1'],
  description: {
    grade: 'poor',
    score: 30,
    flags: ['sparse'],
    matchable: 'yes',
    needsImprovement: true,
    status: null,
    confidence: null,
    matchedDistanceM: null,
    current: false,
    decision: null,
    reason: null,
    ...r,
  },
  observedShare: null,
  inferredShare: null,
});

const detail = (
  s: Partial<ActivitySummary>,
  r: ActivityRouteInfo | null,
): Pick<ActivityDetail, 'summary' | 'route'> => ({
  summary: summary(s),
  route: r,
});

beforeEach(() => {
  Object.values(client).forEach((m) => m.mockReset());
});

describe('RouteLine', () => {
  it('renders nothing when route quality is off', () => {
    const { container } = render(
      <RouteLine detail={detail({}, null)} view="roads" onView={jest.fn()} onChanged={jest.fn()} />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('shows a matched route with the toggle and revert, and passes axe', async () => {
    const onView = jest.fn();
    const onChanged = jest.fn().mockResolvedValue(null);
    client.routesDecide.mockResolvedValue(undefined);
    const { container } = render(
      <RouteLine
        detail={detail({ geometry: 'matched' }, route({ confidence: 0.92, status: 'matched' }))}
        view="roads"
        onView={onView}
        onChanged={onChanged}
      />,
    );
    expect(screen.getByText('Route: matched to roads · 92%')).toBeInTheDocument();
    expect(screen.queryByText('Fix this route')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Recorded' }));
    expect(onView).toHaveBeenCalledWith('recorded');
    expect(await axe(container)).toHaveNoViolations();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Revert to recorded' }));
    });
    expect(client.routesDecide).toHaveBeenCalledWith('a1', 'reject');
    expect(onChanged).toHaveBeenCalled();
  });

  it('shows the grade with flags on hover and fixes the route, polling the job', async () => {
    jest.useFakeTimers();
    try {
      const onView = jest.fn();
      const onChanged = jest.fn().mockResolvedValue({ summary: summary({ geometry: 'matched' }) });
      client.routesImprove.mockResolvedValue({ jobId: 'j1' });
      client.routesJob
        .mockResolvedValueOnce({ id: 'j1', state: 'running', total: 1, done: 0 })
        .mockResolvedValueOnce({ id: 'j1', state: 'done', total: 1, done: 1 });
      render(
        <RouteLine
          detail={detail({}, route())}
          view="roads"
          onView={onView}
          onChanged={onChanged}
        />,
      );
      expect(screen.getByText('Route quality: poor')).toHaveAttribute('title', 'few points');

      await act(async () => {
        fireEvent.click(screen.getByRole('button', { name: 'Fix this route' }));
      });
      expect(client.routesImprove).toHaveBeenCalledWith(['a1']);
      expect(screen.getByText('Waiting for the route service…')).toBeInTheDocument();
      await act(async () => {
        await jest.advanceTimersByTimeAsync(2000);
      });
      expect(screen.getByText('Matching to roads…')).toBeInTheDocument();
      await act(async () => {
        await jest.advanceTimersByTimeAsync(2000);
      });
      expect(client.routesJob).toHaveBeenCalledTimes(2);
      expect(onChanged).toHaveBeenCalled();
      expect(onView).toHaveBeenCalledWith('roads');
    } finally {
      jest.useRealTimers();
    }
  });

  it('"Try again" forgets the reject before matching', async () => {
    client.routesClearDecision.mockResolvedValue(undefined);
    client.routesImprove.mockRejectedValue(
      new api.ApiRequestError('down', 503, 'ROUTES_UNAVAILABLE'),
    );
    render(
      <RouteLine
        detail={detail({}, route({ decision: 'reject' }))}
        view="roads"
        onView={jest.fn()}
        onChanged={jest.fn()}
      />,
    );
    expect(screen.getByText('Route: kept as recorded')).toBeInTheDocument();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    });
    expect(client.routesClearDecision).toHaveBeenCalledWith('a1');
    expect(screen.getByText('Route matching unavailable')).toBeInTheDocument();
  });

  it('says why when "Fix this route" still lands below the threshold', async () => {
    jest.useFakeTimers();
    try {
      const after = route({
        status: 'low_confidence',
        confidence: 0.65,
        reason: 'low confidence 0.65: a 6 km stretch with no road that fits the 4 min elapsed',
      });
      const onChanged = jest.fn().mockResolvedValue({ summary: summary(), route: after });
      client.routesImprove.mockResolvedValue({ jobId: 'j2' });
      client.routesJob.mockResolvedValueOnce({ id: 'j2', state: 'done', total: 1, done: 1 });
      render(
        <RouteLine
          detail={detail({}, route())}
          view="roads"
          onView={jest.fn()}
          onChanged={onChanged}
        />,
      );
      await act(async () => {
        fireEvent.click(screen.getByRole('button', { name: 'Fix this route' }));
      });
      await act(async () => {
        await jest.advanceTimersByTimeAsync(2000);
      });
      expect(
        screen.getByText(
          'No confident road match (65%): a 6 km stretch with no road that fits the 4 min elapsed',
        ),
      ).toBeInTheDocument();
    } finally {
      jest.useRealTimers();
    }
  });
});
