import React from 'react';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { axe } from 'jest-axe';
import PlaceReviewBlock from '../PlaceReview';
import ReviewsSection from '../ReviewsSection';
import { dueText, overviewText, reminderText, reviewedText } from '../reviewText';
import type { PlaceDetail, PlaceReview, ReviewsOverview } from '../types';

jest.mock('../../../../services/api/BackendApiClient', () => ({
  backendApiClient: {
    atlasReviewReminder: jest.fn(),
    atlasReviews: jest.fn(),
  },
}));

// eslint-disable-next-line @typescript-eslint/no-var-requires
const api = jest.requireMock('../../../../services/api/BackendApiClient');
const client = api.backendApiClient as Record<string, jest.Mock>;

const review = (r: Partial<PlaceReview> = {}): PlaceReview => ({
  id: 'r1',
  name: 'Hummus Abu Hassan',
  address: 'Tel Aviv',
  rating: 4,
  text: 'Great hummus',
  published: true,
  reviewedAt: '2025-09-03T07:12:00Z',
  mapsUrl: 'http://maps.google.com/?cid=1',
  answers: [{ question: 'Price per person', answer: '₪40–60' }],
  placeId: 'p1',
  linkedBy: 'name',
  lat: 32.08,
  lon: 34.78,
  ...r,
});

const place = (p: Partial<PlaceDetail> = {}): PlaceDetail => ({
  id: 'p1',
  name: 'Falafel Hakosem',
  address: null,
  category: 'Fast food',
  lat: 32.07,
  lon: 34.77,
  notes: null,
  source: 'google-timeline',
  linkedRef: null,
  website: null,
  phone: null,
  openingHours: null,
  details: {},
  visits: 2,
  firstVisit: null,
  lastVisit: null,
  hoursSpent: 1,
  identifyState: 'auto',
  candidates: [],
  identifiedFrom: null,
  ...p,
});

const overview = (o: Partial<ReviewsOverview> = {}): ReviewsOverview => ({
  written: 12,
  average: 4.25,
  linked: 9,
  unlinked: 3,
  recent: [review(), review({ id: 'r2', placeId: null, name: 'Far away bar', rating: 2 })],
  due: [
    {
      placeId: 'p2',
      name: 'Gordon Beach',
      category: 'Beach',
      lastVisit: '2026-10-21T10:00:00Z',
      visits: 1,
      writeUrl:
        'https://www.google.com/maps/search/?api=1&query=Gordon%20Beach&query_place_id=ChIJb',
      lat: 32.08,
      lon: 34.76,
    },
  ],
  dueTotal: 43,
  ...o,
});

beforeEach(() => jest.clearAllMocks());

describe('review texts', () => {
  it('says when, what is due, and the summary', () => {
    expect(reviewedText(review())).toMatch(/^Reviewed 3 Sept? 2025$/);
    expect(reviewedText(review({ published: false }))).toMatch(/^Draft, never published/);
    expect(reminderText({ state: 'never', category: true })).toBe(
      'No review reminders for this kind of place',
    );
    expect(reminderText({ state: 'later', until: '2026-12-03T00:00:00Z' })).toMatch(
      /hidden until 3 Dec 2026/,
    );
    expect(reminderText({ state: 'reviewed' })).toBeNull();
    expect(reminderText({ state: 'not_reviewable', reason: 'category' })).toBeNull();
    expect(overviewText(overview())).toBe('12 reviews · ★ 4.3 · 9 at places you visited');
    expect(overviewText(overview({ written: 0, average: null, linked: 0 }))).toBe('0 reviews');
    expect(dueText(null, '2026-10-21T10:00:00Z', 1)).toBe('last 21 Oct 2026 · 1 visit');
  });
});

describe('the place panel block', () => {
  it('shows the review with its stars, answers and link', async () => {
    const { container } = render(
      <PlaceReviewBlock place={place({ review: review() })} onState={jest.fn()} />,
    );

    expect(screen.getAllByRole('img', { name: '4 of 5 stars' }).length).toBeGreaterThan(0);
    expect(screen.getByText('Great hummus')).toBeInTheDocument();
    expect(screen.getByText('Price per person: ₪40–60')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Open on Google Maps' })).toHaveAttribute(
      'href',
      'http://maps.google.com/?cid=1',
    );
    expect(await axe(container)).toHaveNoViolations();
  });

  it('reminds, and sets the reminder aside', async () => {
    const onState = jest.fn();
    client.atlasReviewReminder.mockResolvedValue({ state: 'never', category: true });
    render(
      <PlaceReviewBlock
        place={place({
          reviewState: { state: 'to_review', writeUrl: 'https://maps.example/write' },
        })}
        onState={onState}
      />,
    );

    expect(screen.getByText('You have not reviewed this place')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Write a review' })).toHaveAttribute(
      'href',
      'https://maps.example/write',
    );
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Not this kind' })));

    expect(client.atlasReviewReminder).toHaveBeenCalledWith('p1', 'never_kind', false);
    expect(onState).toHaveBeenCalledWith({ state: 'never', category: true });
  });

  it('brings a set-aside reminder back, with its kind', async () => {
    client.atlasReviewReminder.mockResolvedValue({ state: 'to_review' });
    render(
      <PlaceReviewBlock
        place={place({ reviewState: { state: 'never', category: true } })}
        onState={jest.fn()}
      />,
    );

    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Remind me again' })));

    expect(client.atlasReviewReminder).toHaveBeenCalledWith('p1', 'clear', true);
  });

  it('says nothing about a place that is not one to review', () => {
    const { container } = render(
      <PlaceReviewBlock
        place={place({ reviewState: { state: 'not_reviewable', reason: 'category' } })}
        onState={jest.fn()}
      />,
    );
    expect(container).toBeEmptyDOMElement();
  });
});

describe('the Insights section', () => {
  it('lists the places to review and the latest reviews, and acts on a reminder', async () => {
    const onShowPlace = jest.fn();
    client.atlasReviews.mockResolvedValue(overview());
    client.atlasReviewReminder.mockResolvedValue({ state: 'later' });
    const { container } = render(<ReviewsSection onShowPlace={onShowPlace} />);
    await act(async () => {});

    const section = screen.getByRole('region', { name: 'Reviews' });
    expect(within(section).getByText('To review (43)')).toBeInTheDocument();
    expect(
      within(section).getByRole('link', { name: 'Write a review of Gordon Beach' }),
    ).toHaveAttribute('href', expect.stringContaining('query_place_id=ChIJb'));
    fireEvent.click(within(section).getByText('Gordon Beach'));
    expect(onShowPlace).toHaveBeenCalledWith(32.08, 34.76);
    // An unlinked review opens Google Maps
    expect(within(section).getByText('Far away bar').closest('a')).toHaveAttribute(
      'href',
      'http://maps.google.com/?cid=1',
    );
    expect(await axe(container)).toHaveNoViolations();

    fireEvent.click(
      within(section).getByRole('button', { name: 'Reminder choices for Gordon Beach' }),
    );
    await act(async () =>
      fireEvent.click(screen.getByRole('menuitem', { name: 'Later (30 days)' })),
    );
    expect(client.atlasReviewReminder).toHaveBeenCalledWith('p2', 'later');
    expect(client.atlasReviews).toHaveBeenCalledTimes(2);
  });

  it('is absent before any review or reminder', async () => {
    client.atlasReviews.mockResolvedValue(
      overview({ written: 0, dueTotal: 0, due: [], recent: [] }),
    );
    const { container } = render(<ReviewsSection onShowPlace={jest.fn()} />);
    await act(async () => {});
    expect(container).toBeEmptyDOMElement();
  });
});
