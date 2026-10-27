import {
  detailRows,
  formatCoordinates,
  googleMapsUrl,
  isUnnamedPlace,
  nameFromAddress,
  openStreetMapUrl,
  osmObjectUrl,
  safeWebUrl,
  shortUrl,
  telUrl,
  wikipediaUrl,
} from '../placeText';
import { formatMeters } from '../fusionText';
import type { LookupAddress } from '../types';

const address = (patch: Partial<LookupAddress> = {}): LookupAddress => ({
  display: '12 Herzl St, Rehovot, Israel',
  houseNumber: '12',
  road: 'Herzl St',
  suburb: null,
  city: 'Rehovot',
  state: null,
  postcode: null,
  country: 'Israel',
  countryCode: 'il',
  ...patch,
});

describe('atlas place text', () => {
  it('formats coordinates with 5 decimals', () => {
    expect(formatCoordinates(31.810613, 34.774228)).toBe('31.81061, 34.77423');
    expect(formatCoordinates(-33.9, 151)).toBe('-33.90000, 151.00000');
  });

  it('links to Google Maps and OpenStreetMap at a point', () => {
    expect(googleMapsUrl(31.81061, 34.77423)).toBe(
      'https://www.google.com/maps/search/?api=1&query=31.810610,34.774230',
    );
    expect(openStreetMapUrl(31.81061, 34.77423)).toBe(
      'https://www.openstreetmap.org/?mlat=31.810610&mlon=34.774230#map=18/31.810610/34.774230',
    );
  });

  it('links an OpenStreetMap ref to its page', () => {
    expect(osmObjectUrl('osm:node/123456')).toBe('https://www.openstreetmap.org/node/123456');
    expect(osmObjectUrl('osm:way/42')).toBe('https://www.openstreetmap.org/way/42');
    expect(osmObjectUrl('osm:relation/7')).toBe('https://www.openstreetmap.org/relation/7');
    expect(osmObjectUrl('google:abc')).toBeNull();
    expect(osmObjectUrl('osm:node/abc')).toBeNull();
    expect(osmObjectUrl(null)).toBeNull();
  });

  it('links a wikipedia tag to the article', () => {
    expect(wikipediaUrl('en:Weizmann Institute of Science')).toBe(
      'https://en.wikipedia.org/wiki/Weizmann_Institute_of_Science',
    );
    expect(wikipediaUrl('he:רחובות')).toBe(
      `https://he.wikipedia.org/wiki/${encodeURIComponent('רחובות')}`,
    );
    expect(wikipediaUrl('Just a title')).toBeNull();
    expect(wikipediaUrl('en:')).toBeNull();
    expect(wikipediaUrl(undefined)).toBeNull();
  });

  it('detects unnamed places', () => {
    ['', '  ', 'Place', 'Unnamed place', 'saved place', 'Searched Place', null, undefined].forEach(
      (n) => expect(isUnnamedPlace(n)).toBe(true),
    );
    ['Home', 'Cafe Landwer', 'קפה לנדוור'].forEach((n) => expect(isUnnamedPlace(n)).toBe(false));
  });

  it('names a place after its road and number', () => {
    expect(nameFromAddress(address())).toBe('Herzl St 12');
    expect(nameFromAddress(address({ houseNumber: null }))).toBe('Herzl St');
    expect(nameFromAddress(address({ road: null }))).toBe('12 Herzl St');
  });

  it('formats distances', () => {
    expect(formatMeters(35.4)).toBe('35 m');
    expect(formatMeters(0)).toBe('0 m');
    expect(formatMeters(1260)).toBe('1.3 km');
  });

  it('makes only safe website links', () => {
    expect(safeWebUrl('https://cafe.example/menu')).toBe('https://cafe.example/menu');
    expect(safeWebUrl('cafe.example')).toBe('https://cafe.example');
    expect(safeWebUrl('javascript:alert(1)')).toBeNull();
    expect(safeWebUrl('')).toBeNull();
    expect(shortUrl('https://www.cafe.example/')).toBe('www.cafe.example');
  });

  it('makes tel links from the first number', () => {
    expect(telUrl('+972 8-123 4567; +972 50 1234567')).toBe('tel:+97281234567');
    expect(telUrl('none')).toBeNull();
    expect(telUrl(null)).toBeNull();
  });

  it('lists the useful details', () => {
    expect(
      detailRows({
        wikidata: 'Q1',
        wikipedia: 'en:Rehovot',
        cuisine: 'pizza;italian',
        brand: 'Landwer',
        operator: ' ',
      }),
    ).toEqual([
      { key: 'cuisine', label: 'Cuisine', value: 'pizza, italian' },
      { key: 'brand', label: 'Brand', value: 'Landwer' },
      {
        key: 'wikipedia',
        label: 'Wikipedia',
        value: 'Rehovot',
        href: 'https://en.wikipedia.org/wiki/Rehovot',
      },
    ]);
    expect(detailRows({ wikipedia: 'Rehovot' })).toEqual([
      { key: 'wikipedia', label: 'Wikipedia', value: 'Rehovot' },
    ]);
    expect(detailRows(null)).toEqual([]);
  });
});
