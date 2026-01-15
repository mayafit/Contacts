const featuresFlags = require('./features-flags-prod.json');

module.exports = {
  setupFiles: ['raf/polyfill', 'jest-canvas-mock', './jest.polyfills.js'],
  setupFilesAfterEnv: ['./jest.setup.js'],
  verbose: true,
  testEnvironmentOptions: {
    url: 'http://localhost/',
    customExportConditions: [''],
  },

  testPathIgnorePatterns: ['/node_modules/', '/dist/'],
  reporters: [
    'default',
    [
      'jest-junit',
      {
        outputDirectory: './test-reports',
      },
    ],
    [
      'jest-html-reporters',
      {
        publicPath: './test-reports',
      },
    ],
  ],
  collectCoverage: true,
  coverageReporters: ['json', 'html', 'text', 'cobertura'],
  collectCoverageFrom: ['src/**/*.{js,jsx,ts,tsx}'],
  moduleNameMapper: {
    '\\.(css|less|sass|scss)$': '<rootDir>/__mocks__/styleMock.js',
    '^bfSrc/(.*)$': '<rootDir>/blueFiberSrc/$1',
    '^src/(.*)$': '<rootDir>/src/$1',
    '^lodash-es$': 'lodash',
  },
  moduleFileExtensions: ['js', 'jsx', 'ts', 'tsx'],
  transform: {
    '^.+\\.(ts|tsx)$': ['ts-jest', { isolateModules: false }],
    '^.+\\.(js|jsx)$': 'babel-jest',
  },
  extensionsToTreatAsEsm: ['.ts', '.tsx', '.jsx'],
  testEnvironment: 'jest-fixed-jsdom',
  preset: 'ts-jest/presets/default-esm',
  testRegex: '(/__tests__/.*|\\.(test|spec))\\.(js|jsx|ts|tsx)$',
  // transformIgnorePatterns: ['<rootDir>/node_modules/(?!(geodesy/.*|@mui/material/.*))'],
  globals: {
    FEATURES_FLAGS: featuresFlags,
    'ts-jest': {
      useESM: true,
    },
  },
};
