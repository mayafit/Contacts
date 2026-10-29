import React from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { axe } from 'jest-axe';
import RouteRebuild, { type RebuildState } from '../RouteRebuild';
import type {
  ActivityDetail,
  ActivityRouteInfo,
  ActivitySummary,
  RebuildSuggestion,
} from '../types';

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
      routesRebuild: jest.fn(),
      routesApply: jest.fn(),
      routesJob: jest.fn(),
      routesClearWaypoints: jest.fn(),
    },
  };
});

// eslint-disable-next-line @typescript-eslint/no-var-requires
const api = jest.requireMock('../../../../services/api/BackendApiClient');
const client = api.backendApiClient as Record<string, jest.Mock>;

const detail = (
  r: Partial<NonNullable<ActivityRouteInfo['description']>> = {},
  recordIds = ['a1'],
): Pick<ActivityDetail, 'summary' | 'route'> => ({
  summary: {
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
  } as ActivitySummary,
  route: {
    recordIds,
    description: {
      grade: 'poor',
      score: 30,
      flags: ['sparse'],
      matchable: 'yes',
      needsImprovement: true,
      status: 'low_confidence',
      confidence: 0.52,
      matchedDistanceM: null,
      current: true,
      decision: null,
      reason: null,
      ...r,
    },
    observedShare: null,
    inferredShare: null,
  },
});

const suggested: RebuildSuggestion = {
  method: 'similar',
  outcome: 'suggested',
  activityId: 'a1',
  status: 'matched',
  confidence: 0.85,
  previousConfidence: 0.52,
  polyline: '_p~iF~ps|U_ulLnnqC',
  pieces: [{ fromM: 0, toM: 100, kind: 'inferred' }],
  basis: {
    activityId: 'b1',
    start: '2026-09-03T05:12:00Z',
    reversed: false,
    fit: 0.9,
    candidates: 3,
    agreeing: 3,
    examples: [],
  },
};

beforeEach(() => {
  jest.useFakeTimers();
  Object.values(client).forEach((m) => m.mockReset());
});

afterEach(() => jest.useRealTimers());

/** Lets the rebuild's job poll run once */
const poll = async () => {
  await act(async () => {
    await Promise.resolve();
    jest.advanceTimersByTime(2100);
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
  });
};

describe('RouteRebuild', () => {
  it('is hidden for an outing of several recordings and for what cannot be matched', () => {
    const props = {
      suggestion: null,
      onSuggestion: jest.fn(),
      marking: null,
      onMarking: jest.fn(),
      onApplied: jest.fn(),
    };
    const { container, rerender } = render(
      <RouteRebuild detail={detail({}, ['a1', 'a2'])} {...props} />,
    );
    expect(container).toBeEmptyDOMElement();
    rerender(<RouteRebuild detail={detail({ matchable: 'type_never' })} {...props} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('rebuilds from similar drives and hands the suggestion up', async () => {
    client.routesRebuild.mockResolvedValue({ jobId: 'j1' });
    client.routesJob.mockResolvedValue({ id: 'j1', state: 'done', result: suggested });
    const onSuggestion = jest.fn();
    const onMarking = jest.fn();
    render(
      <RouteRebuild
        detail={detail()}
        suggestion={null}
        onSuggestion={onSuggestion}
        marking={null}
        onMarking={onMarking}
        onApplied={jest.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'from similar drives' }));
    expect(client.routesRebuild).toHaveBeenCalledWith('a1', 'similar', undefined);
    expect(screen.getByRole('status')).toHaveTextContent('Looking for similar drives');
    await poll();

    expect(onSuggestion).toHaveBeenLastCalledWith({
      activityId: 'a1',
      jobId: 'j1',
      result: suggested,
    });
    expect(onMarking).toHaveBeenLastCalledWith(null);
  });

  it('says why when no similar drive fits, and offers marking the way', async () => {
    client.routesRebuild.mockResolvedValue({ jobId: 'j1' });
    client.routesJob.mockResolvedValue({
      id: 'j1',
      state: 'done',
      result: {
        method: 'similar',
        outcome: 'no_similar',
        activityId: 'a1',
        message: 'None of the 3 drives follows this one.',
      },
    });
    const onMarking = jest.fn();
    render(
      <RouteRebuild
        detail={detail({ waypoints: [[32, 34.8]] })}
        suggestion={null}
        onSuggestion={jest.fn()}
        marking={null}
        onMarking={onMarking}
        onApplied={jest.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'from similar drives' }));
    await poll();

    expect(screen.getByRole('status')).toHaveTextContent(
      'No similar drive found: None of the 3 drives follows this one.',
    );
    expect(screen.getByRole('button', { name: 'clear them' })).toBeInTheDocument();
    // The points marked before are offered again
    fireEvent.click(screen.getByRole('button', { name: 'change the 1 marked points' }));
    expect(onMarking).toHaveBeenLastCalledWith([[32, 34.8]]);
  });

  it('rebuilds through the marked points, and cancels', async () => {
    client.routesRebuild.mockResolvedValue({ jobId: 'j2' });
    client.routesJob.mockResolvedValue({
      id: 'j2',
      state: 'done',
      result: { ...suggested, method: 'waypoints', waypoints: [[32.1, 34.81]] },
    });
    const onMarking = jest.fn();
    const onSuggestion = jest.fn();
    const { container } = render(
      <RouteRebuild
        detail={detail()}
        suggestion={null}
        onSuggestion={onSuggestion}
        marking={[[32.1, 34.81]]}
        onMarking={onMarking}
        onApplied={jest.fn()}
      />,
    );

    expect(screen.getByRole('group', { name: 'Mark the way' })).toHaveTextContent('1 of 50 points');
    jest.useRealTimers();
    expect(await axe(container)).toHaveNoViolations();
    jest.useFakeTimers();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onMarking).toHaveBeenLastCalledWith(null);

    fireEvent.click(screen.getByRole('button', { name: 'Rebuild' }));
    expect(client.routesRebuild).toHaveBeenCalledWith('a1', 'waypoints', [[32.1, 34.81]]);
    await poll();
    expect(onSuggestion).toHaveBeenLastCalledWith(expect.objectContaining({ jobId: 'j2' }));
  });

  it('shows a suggestion, uses it, and discards it', async () => {
    client.routesApply.mockResolvedValue(undefined);
    const onSuggestion = jest.fn();
    const onApplied = jest.fn().mockResolvedValue(null);
    const state: RebuildState = { activityId: 'a1', jobId: 'j1', result: suggested };
    render(
      <RouteRebuild
        detail={detail()}
        suggestion={state}
        onSuggestion={onSuggestion}
        marking={null}
        onMarking={jest.fn()}
        onApplied={onApplied}
      />,
    );

    const card = screen.getByRole('group', { name: 'Suggested route' });
    expect(card).toHaveTextContent('From 3 drives between the same places, the closest on 3 Sep');
    expect(card).toHaveTextContent('85% confidence (now 52%)');

    fireEvent.click(screen.getByRole('button', { name: 'Discard' }));
    expect(onSuggestion).toHaveBeenLastCalledWith(null);

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Use this route' }));
    });
    expect(client.routesApply).toHaveBeenCalledWith('a1', 'j1');
    expect(onApplied).toHaveBeenCalled();
  });

  it('shows a conflict when the drive changed after the rebuild', async () => {
    client.routesApply.mockRejectedValue(
      new api.ApiRequestError(
        'The drive changed after the rebuild; rebuild it again.',
        409,
        'CONFLICT',
      ),
    );
    const state: RebuildState = { activityId: 'a1', jobId: 'j1', result: suggested };
    render(
      <RouteRebuild
        detail={detail()}
        suggestion={state}
        onSuggestion={jest.fn()}
        marking={null}
        onMarking={jest.fn()}
        onApplied={jest.fn()}
      />,
    );

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Use this route' }));
    });
    expect(screen.getByRole('status')).toHaveTextContent(
      'The drive changed after the rebuild; rebuild it again.',
    );
  });
});

describe('RouteRebuild clearing', () => {
  it('clears the marked points and reloads', async () => {
    client.routesClearWaypoints.mockResolvedValue(undefined);
    const onApplied = jest.fn().mockResolvedValue(null);
    render(
      <RouteRebuild
        detail={detail({
          waypoints: [
            [32, 34.8],
            [32.1, 34.8],
          ],
        })}
        suggestion={null}
        onSuggestion={jest.fn()}
        marking={null}
        onMarking={jest.fn()}
        onApplied={onApplied}
      />,
    );

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'clear them' }));
    });
    expect(client.routesClearWaypoints).toHaveBeenCalledWith('a1');
    expect(onApplied).toHaveBeenCalled();
    expect(screen.getByRole('status')).toHaveTextContent('Marked points cleared');
  });
});
