const path = require('path');
const CopyWebpackPlugin = require('copy-webpack-plugin');

// your app's webpack.config.js
const custom = require('../blueFiberSrc/_webpack.bf.dev');
const toPath = (filePath) => path.resolve(process.cwd(), filePath);

module.exports = {
  framework: {
    name: '@storybook/react-webpack5',
    options: {},
  },
  stories: [
    '../src/**/*.stories.[tj]s',
    '../src/**/*.stories.[tj]sx',
    '../blueFiberSrc/**/*.stories.[tj]s',
    '../blueFiberSrc/**/*.stories.[tj]sx',
  ],
  docs: {
    autodocs: 'tag',
  },
  // stories: ['../stories/**/*.stories.[tj]s', '../**/*.stories.[tj]s'],
  // core: {
  //   builder: 'webpack5'
  // },
  addons: [
    '@storybook/addon-links',
    '@storybook/addon-essentials', // includes actions,backgrounds,controls, docs, viewport, toolbars, measue, outline
    '@storybook/addon-themes',
    'storybook-addon-performance',
  ],
  webpackFinal: async (config) => {
    // config.module.rules[0].exclude = [config.module.rules[0].exclude];

    // remove the html/mf plugin!
    custom.plugins = custom.plugins.filter(
      (plugin) =>
        plugin?.userOptions?.template !== 'blueFiberSrc/index.hbs' &&
        plugin.constructor.name !== 'ModuleFederationPlugin',
    );

    // config.module.rules[3].include = custom.module.rules[2].include;
    // config.module.rules[3].exclude = config.module.rules[0].exclude;

    config.module.rules = [
      ...config.module.rules.filter((rule) => rule?.test?.toString() !== '/\\.css$/'),
      ...custom.module.rules.filter((rule) => !rule?.test?.toString()?.includes('hbs')),
    ];

    return {
      ...config,
      plugins: [
        ...config.plugins,
        ...custom.plugins,
        // new CopyWebpackPlugin([

        // ]),
      ],
      resolve: {
        ...config.resolve,
        alias: {
          ...config.resolve.alias,
          ...custom.resolve.alias,
          '@emotion/core': toPath('node_modules/@emotion/react'),
          'emotion-theming': toPath('node_modules/@emotion/react'),
        },
      },
    };
  },
};
