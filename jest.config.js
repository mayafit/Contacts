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
  coverageReporters: ['json', 'html', 'text', 'lcov', 'cobertura'],
  coverageDirectory: './coverage',
  collectCoverageFrom: [
    'src/entries/Contacts/**/*.{ts,tsx}',
    '!src/entries/Contacts/**/*.stories.{ts,tsx}',
    '!src/entries/Contacts/**/*.test.{ts,tsx}',
    '!src/entries/Contacts/**/index.{ts,tsx}',
    '!src/entries/Contacts/**/__tests__/**',
    '!src/entries/Contacts/**/__mocks__/**',
  ],
  coverageThreshold: {
    // Global thresholds - will be enforced when code exists
    global: {
      branches: 50,
      functions: 50,
      lines: 50,
      statements: 50,
    },
  },
  moduleNameMapper: {
    '\\.(css|less|sass|scss)$': '<rootDir>/__mocks__/styleMock.js',
    '^bfSrc/(.*)$': '<rootDir>/blueFiberSrc/$1',
    '^src/(.*)$': '<rootDir>/src/$1',
    '^lodash-es$': 'lodash',
  },
  moduleFileExtensions: ['js', 'jsx', 'ts', 'tsx'],
  transform: {
    '^.+\\.(ts|tsx)$': [
      'ts-jest',
      {
        isolatedModules: true,
        useESM: true,
        tsconfig: './tsconfig.json',
      },
    ],
    '^.+\\.(js|jsx)$': 'babel-jest',
  },
  extensionsToTreatAsEsm: ['.ts', '.tsx', '.jsx'],
  testEnvironment: 'jest-fixed-jsdom',
  preset: 'ts-jest/presets/default-esm',
  testRegex: '(/__tests__/.*|\\.(test|spec))\\.(js|jsx|ts|tsx)$',
  transformIgnorePatterns: [
    '/node_modules/(?!(@mui|@emotion|@reduxjs|@babel/runtime|@react-oauth)/)',
  ],
  globals: {
    FEATURES_FLAGS: featuresFlags,
  },
};
