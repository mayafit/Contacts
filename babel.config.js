const bluefiberConfig = require('./bluefiber.config');

const plugins = [
  [
    '@babel/transform-runtime',
    {
      corejs: 3,
    },
  ],
];
const presets = [
  [
    '@babel/preset-env',
    {
      targets: {
        chrome: 90,
        browsers: '>1%, not ie 11, not op_mini all',
        node: 'current',
      },
    },
  ],
  '@babel/preset-react',
];

if (process.env.E2E === 'false') {
  console.log('babel in production - remove e2e attributes');
  plugins.push(['react-remove-properties', { properties: bluefiberConfig.testAttributes }]);
} else {
  console.log('keep e2e attributes');
}

module.exports = { plugins, presets };
