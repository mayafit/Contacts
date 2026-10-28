import { photoCountText, placePhotosText, placedText, spread, takenText } from '../photos';
import type { PhotoSummary } from '../types';

const photo = (placedBy: PhotoSummary['placedBy']): PhotoSummary => ({
  id: '1',
  name: 'IMG_1.jpg',
  takenAt: '2026-09-13T08:15:00Z',
  isVideo: false,
  lat: 31.8,
  lon: 34.8,
  placedBy,
  width: 4000,
  height: 3000,
  webUrl: null,
  activityId: null,
  visitId: null,
  camera: null,
});

describe('photos', () => {
  it('spreads a long list evenly, keeping the first and last', () => {
    const items = Array.from({ length: 100 }, (_, i) => i);
    const picked = spread(items, 5);
    expect(picked).toEqual([0, 25, 50, 74, 99]);
    expect(spread([1, 2, 3], 5)).toEqual([1, 2, 3]);
    expect(spread([1, 2, 3], 1)).toEqual([1]);
  });

  it('says how a photo was placed', () => {
    expect(placedText(photo('gps'))).toBe('Position from the camera');
    expect(placedText(photo('track'))).toBe('Placed on the track by its time');
    expect(placedText(photo('visit'))).toBe('Placed at the visit of its time');
    expect(placedText(photo(null))).toBeNull();
  });

  it('counts photos, and says nothing when there are none', () => {
    expect(photoCountText(1)).toBe('1 photo');
    expect(photoCountText(1200)).toBe(`${(1200).toLocaleString()} photos`);
    expect(photoCountText(0)).toBeNull();
    expect(photoCountText(undefined)).toBeNull();
    expect(photoCountText(500, true)).toBe('500+ photos');
  });

  it("says which of a place's photos are from the open visit", () => {
    expect(placePhotosText(3, 3)).toBe('3 photos');
    expect(placePhotosText(1, 12)).toBe('1 photo (12 at this place)');
    expect(placePhotosText(0, 12)).toBe('12 photos at this place');
    expect(placePhotosText(4, 500, true)).toBe('4 photos (500+ at this place)');
    expect(placePhotosText(0, 500, true)).toBe('500+ photos at this place');
  });

  it('writes when it was taken with the day', () => {
    expect(takenText('2026-09-13T08:15:00Z')).toMatch(/13 Sept? 2026/);
  });
});
