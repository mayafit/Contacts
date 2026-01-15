import { divAB } from '../utils';

describe('utils.js', () => {
  describe('divAB', () => {
    it('should divide 2 positive numbers', () => {
      expect(divAB({ a: 6, b: 2 })).toBe(3);
    });
    it('should divide 2 neagative numbers', () => {
      expect(divAB({ a: -8, b: -4 })).toBe(2);
    });
  });
});
