import { isGoogleReviews, merge, parseGoogleReviews } from '../google';
import { kindOf, parseFile } from '..';

/** Shaped like Takeout's Maps (your places)/Reviews.json (Data Portability schema maps.reviews) */
const REVIEWS = {
  type: 'FeatureCollection',
  features: [
    {
      type: 'Feature',
      geometry: { type: 'Point', coordinates: [34.7818, 32.0853] },
      properties: {
        date: '2025-09-03T07:12:00.123Z',
        five_star_rating_published: 5,
        review_text_published: 'Great hummus.\nGo early.',
        google_maps_url: 'http://maps.google.com/?cid=1234567890',
        location: {
          name: 'Hummus Abu Hassan',
          address: 'HaDolfin St 1, Tel Aviv-Yafo',
          country_code: 'IL',
        },
        questions: [
          { question: 'Price per person', selected_option: '₪40–60' },
          { question: 'Meal type', selected_options: ['Lunch', 'Brunch'] },
          { question: 'Recommended dishes', text_answer: 'Masabacha' },
          { question: 'Food', rating: 5 },
          { question: 'Empty' },
        ],
      },
    },
    {
      type: 'Feature',
      geometry: { type: 'Point', coordinates: [34.8, 32.1] },
      properties: {
        date: '2026-01-02T10:00:00Z',
        five_star_rating_draft: 3,
        review_text_draft: 'Not sure yet',
        google_maps_url: 'http://maps.google.com/?cid=42',
        // Some exports write the location as a one-element array
        location: [{ name: 'קפה לנדוור', address: 'Tel Aviv' }],
      },
    },
    {
      type: 'Feature',
      geometry: { type: 'Point', coordinates: [34.9, 32.2] },
      properties: { date: '2026-01-02T10:00:00Z', location: { name: 'Nothing written' } },
    },
    {
      type: 'Feature',
      geometry: { type: 'Point', coordinates: [34.9, 32.2] },
      properties: { five_star_rating_published: 4 },
    },
  ],
};

describe('Google Maps reviews (Takeout)', () => {
  it('reads published reviews, drafts and the answers, and skips empty ones', () => {
    const parsed = parseGoogleReviews(REVIEWS);

    expect(parsed.source).toBe('google-maps');
    expect(parsed.reviews).toHaveLength(2);
    expect(parsed.reviews![0]).toEqual({
      name: 'Hummus Abu Hassan',
      address: 'HaDolfin St 1, Tel Aviv-Yafo',
      countryCode: 'IL',
      lat: 32.0853,
      lon: 34.7818,
      rating: 5,
      text: 'Great hummus.\nGo early.',
      published: true,
      reviewedAt: '2025-09-03T07:12:00.123Z',
      mapsUrl: 'http://maps.google.com/?cid=1234567890',
      answers: [
        { question: 'Price per person', answer: '₪40–60' },
        { question: 'Meal type', answer: 'Lunch, Brunch' },
        { question: 'Recommended dishes', answer: 'Masabacha' },
        { question: 'Food', answer: '5/5' },
      ],
    });
    expect(parsed.reviews![1]).toMatchObject({
      name: 'קפה לנדוור',
      rating: 3,
      text: 'Not sure yet',
      published: false,
    });
  });

  it('recognises the file by its name and by its content, but not saved places', () => {
    expect(kindOf('Takeout/Maps (your places)/Reviews.json')).toBe('json');
    expect(kindOf('Reviews.json')).toBe('json');
    expect(isGoogleReviews(REVIEWS)).toBe(true);
    const saved = {
      type: 'FeatureCollection',
      features: [{ properties: { location: { name: 'Home' } } }],
    };
    expect(isGoogleReviews(saved)).toBe(false);
  });

  it('is parsed from a Takeout path and merged with other files', async () => {
    const parsed = await parseFile({
      path: 'Takeout/Maps (your places)/Reviews.json',
      text: async () => JSON.stringify(REVIEWS),
    });

    expect(parsed?.reviews).toHaveLength(2);
    expect(merge([parsed!, parsed!], 'google-maps').reviews).toHaveLength(4);
  });
});
