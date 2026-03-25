import '@testing-library/jest-dom';
import { toHaveNoViolations } from 'jest-axe';

// Extend Jest matchers with accessibility testing
expect.extend(toHaveNoViolations);

delete global.Cesium;

// jest.mock('uuid', () => {
//   return {
//     v4: jest.fn(() => 'abcd'),
//   };
// });

const packagesToSimpleMock = [
  'allotment',
  'lodash-es/debounce',
  'lodash-es/throttle',
  '@tsparticles/react',
  '@tsparticles/all',
  'react-dnd',
  'react-dnd-html5-backend',
];

packagesToSimpleMock.forEach((pkgName) => {
  jest.mock(
    pkgName,
    () => {
      return {};
    },
    { virtual: true },
  );
});
