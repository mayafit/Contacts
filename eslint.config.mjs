import react from 'eslint-plugin-react';
import prettier from 'eslint-plugin-prettier';
import { fixupPluginRules } from '@eslint/compat';
import reactHooks from 'eslint-plugin-react-hooks';
import typescriptEslint from '@typescript-eslint/eslint-plugin';
import jestDom from 'eslint-plugin-jest-dom';
import testingLibrary from 'eslint-plugin-testing-library';
import reactRedux from 'eslint-plugin-react-redux';
import globals from 'globals';
import tsParser from '@typescript-eslint/parser';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import js from '@eslint/js';
import { FlatCompat } from '@eslint/eslintrc';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const compat = new FlatCompat({
  baseDirectory: __dirname,
  recommendedConfig: js.configs.recommended,
  allConfig: js.configs.all,
});

export default [
  {
    ignores: [
      '**/*.svg',
      '**/node_modules',
      '**/public',
      '**/coverage',
      '**/dist',
      '**/_bmad-output',
      '**/_bmad',
      '**/.storybook',
      '**/blueFiberSrc/_webpack.*.js',
      '**/blueFiberSrc/configureStore.ts',
      '**/webpack.*.js',
      '**/*.config.js',
      '**/*.config.mjs',
      '**/babel.config.js',
      '**/MuiTheme.js',
      '**/bluefiber.config.js',
      '**/jest.polyfills.js',
      '**/jest.setup.js',
      '**/src/GLOBALS.ts',
      '**/src/bluefiber.decs.d.ts',
      '**/src/entries/Todos/**',
      '**/src/external/**',
      '**/src/store/trackSlice.js',
      '**/src/utils/utils.ts',
    ],
  },
  ...compat.extends(
    'eslint:recommended',
    'plugin:react/recommended',
    'plugin:prettier/recommended',
    'plugin:jest-dom/recommended',
    'plugin:testing-library/react',
    'plugin:storybook/recommended',
    'plugin:react-redux/recommended',
    'prettier',
    'plugin:@typescript-eslint/recommended',
    'plugin:@typescript-eslint/eslint-recommended',
  ),
  {
    plugins: {
      react,
      'react-hooks': fixupPluginRules(reactHooks),
      prettier,
      '@typescript-eslint': typescriptEslint,
      'jest-dom': jestDom,
      'testing-library': testingLibrary,
      'react-redux': reactRedux,
    },

    languageOptions: {
      globals: {
        ...globals.browser,
        ...globals.jest,
        FEATURES_FLAGS: true,
      },

      parser: tsParser,
      ecmaVersion: 6,
      //   ecmaVersion: 'latest',
      sourceType: 'module',

      parserOptions: {
        project: './tsconfig.json',
        ecmaFeatures: {
          jsx: true,
        },
      },
    },
    settings: {
      react: {
        version: 'detect',
      },
      'import/resolver': {
        jsconfig: {
          config: 'jsconfig.json',
        },
      },
    },
    rules: {
      curly: ['error', 'all'],

      // Enforce use of Logger class instead of console methods
      'no-console': ['error', { allow: ['warn', 'error'] }],

      // Prefer interfaces over type aliases for object types (Airbnb style)
      '@typescript-eslint/consistent-type-definitions': ['error', 'interface'],

      // Enforce strict equality
      eqeqeq: ['error', 'always'],

      // No unused variables (stricter than default)
      '@typescript-eslint/no-unused-vars': ['error', {
        argsIgnorePattern: '^_',
        varsIgnorePattern: '^_',
      }],

      'no-plusplus': 0,
      'linebreak-style': 0,
      'react/jsx-boolean-value': 0,
      'react/jsx-props-no-spreading': 0,
      'import/prefer-default-export': 0,
      'no-underscore-dangle': 0,
      'jsx-a11y/label-has-associated-control': 0,
      'jsx-a11y/click-events-have-key-events': 0,
      'jsx-a11y/no-static-element-interactions': 0,
      'no-use-before-define': 0,
      '@typescript-eslint/no-use-before-define': ['error'],
      '@typescript-eslint/ban-ts-comment': 0,

      '@typescript-eslint/naming-convention': [
        'error',
        {
          selector: 'variable',
          format: ['camelCase', 'PascalCase', 'UPPER_CASE'],
          leadingUnderscore: 'allow',
        },
      ],

      'react/function-component-definition': 0,

      'no-param-reassign': [
        'error',
        {
          props: false,
        },
      ],

      'no-lonely-if': 0,
      'eslint-disable-next-line react/jsx-no-useless-fragment': 0,

      // Disallow var (use const/let)
      'no-var': 'error',

      // Prefer const over let when variable is never reassigned
      'prefer-const': 'error',
    },
  },
];
