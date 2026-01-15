const path = require('path');

const HtmlWebpackPlugin = require('html-webpack-plugin');
const webpack = require('webpack');
const CopyWebpackPlugin = require('copy-webpack-plugin');
const featuresFlagsProd = require('../features-flags-prod.json');
const featuresFlagsDev = require('../features-flags-dev.json');
const ModulesExpose = require('../MODULES_EXPOSE.json');

const envFeaturesFlagDev = {};
// Object.keys(featuresFlagsDev).forEach((ff) => {
//   if (!!process.env[ff]) {
//     envFeaturesFlagDev[ff] = process.env[ff];
//   }
// });
const envFeaturesFlagProd = {};
// Object.keys(featuresFlagsProd).forEach((ff) => {
//   if (!!process.env[ff]) {
//     envFeaturesFlagProd[ff] = process.env[ff];
//   }
// });

const mergedFeaturesFlagsDev = {
  ...featuresFlagsDev,
  ...envFeaturesFlagDev,
};
const mergedFeaturesFlagsProd = {
  ...featuresFlagsProd,
  ...envFeaturesFlagProd,
};

const mergedFeaturesFlag =
  process.env.NODE_ENV === 'production' ? mergedFeaturesFlagsProd : mergedFeaturesFlagsDev;
// {
//   ...featuresFlags,
//   ...(process.env.NODE_ENV === 'production' ? envFeaturesFlagProd : envFeaturesFlagDev),
// };

const { ModuleFederationPlugin } = webpack.container;

const { DefinePlugin } = webpack;

const getRootDirname = () => {
  return path.resolve(__dirname, '..');
};

module.exports = {
  entry: './blueFiberSrc/index.js',
  output: {
    path: path.resolve(getRootDirname(), 'dist'),
    // publicPath: 'auto',
    // filename: 'bundle.[contenthash].js',
  },
  module: {
    unknownContextCritical: false,
    rules: [
      {
        test: /\.m?js/,
        resolve: {
          fullySpecified: false,
        },
      },

      {
        test: /\.(le|c)ss$/,
        // test: /\.s[ac]ss$/,
        use: [
          {
            loader: 'style-loader',
          },
          {
            loader: 'css-loader',
          },
          {
            loader: 'less-loader',
          },
        ],
        // exclude: [/node_modules\/(?!rc-menu).*/, /olmanager/],
        // include: [
        //   path.resolve(getRootDirname(), 'src'),
        //   path.resolve(getRootDirname(), 'blueFiberSrc'),
        //   path.resolve(getRootDirname(), './node_modules/rc-menu'),
        //   // path.resolve(getRootDirname(), './node_modules/react-toastify'),
        //   path.resolve(getRootDirname(), './node_modules/react-image-gallery'),
        //   path.resolve(getRootDirname(), './node_modules/react-multi-carousel'),
        //   path.resolve(getRootDirname(), './node_modules/vis-timeline'),
        //   path.resolve(getRootDirname(), './node_modules/allotment'),
        // ],
      },
      {
        test: /\.svg$/,
        include: [path.resolve(getRootDirname(), 'src/_images')],
        type: 'asset/source',
        exclude: [/node_modules/],
      },
      {
        test: /\.(woff(2)?|ttf|eot)(\?v=\d+\.\d+\.\d+)?$/,
        type: 'asset',
        generator: {
          filename: 'fonts/[name].[ext]',
        },
        // use: [
        //   {
        //     loader: 'file-loader',
        //     options: {
        //       name: '[name].[ext]',
        //       outputPath: 'fonts/',
        //     },
        //   },
        // ],
        exclude: [/node_modules/],
      },
      {
        test: /\.hbs$/,
        use: [{ loader: 'handlebars-loader' }],
        exclude: [/node_modules/],
      },
    ],
  },

  plugins: [
    new ModuleFederationPlugin({
      name: 'webkit',
      library: { type: 'var', name: 'webkit' },
      filename: 'remoteEntry.js',
      exposes: ModulesExpose,
      shared: {
        react: { singleton: true, requiredVersion: '^18.3.1' },
        'react-dom': {
          singleton: true,
          // eager: false,
          requiredVersion: '^18.3.1',
        },
        'react-redux': {
          singleton: true,
          // eager: false,
          requiredVersion: '^8.1.3',
        },
        '@mui/material': {
          singleton: true,
          // eager: false,
          requiredVersion: '^6.1.6',
        },
        // '@mui/styles': {
        //   singleton: true,
        //   // eager: false,
        //   requiredVersion: '5.11.2',
        // },
        '@mui/styled-engine': {
          singleton: true,
          // eager: false,
          requiredVersion: '^6.1.6',
        },
        // '@mui/lab': {
        //   singleton: true,
        //   // eager: false,
        //   requiredVersion: '5.0.0-alpha.57',
        // },
        // '@emotion/styled': {
        //   singleton: true,
        //   // eager: false,
        //   requiredVersion: '11.11.0',
        // },
        '@emotion/react': {
          singleton: true,
          // eager: false,
          requiredVersion: '^11.13.3',
        },
      },
    }),
    // new ExternalTemplateRemotesPlugin(),
    new CopyWebpackPlugin({
      patterns: [
        { from: 'env.config.json', to: './env.config.json' },
        { from: 'src/assets', to: 'assets', toType: 'dir' },
      ],
    }),

    new HtmlWebpackPlugin({
      title: '' /* 'BlueFiber' */,
      filename: 'index.html',
      template: './public/index.html',
      chunks: ['main'],
    }),
    new DefinePlugin({
      FEATURES_FLAGS: JSON.stringify(mergedFeaturesFlag),
    }),
  ],

  resolve: {
    extensions: ['.js', '.jsx', '.ts', '.tsx'],
    fallback: {
      // stream: false,
      // timers: false,
      timers: require.resolve('timers-browserify'),
      stream: require.resolve('stream-browserify'),
    },
    alias: {
      src: path.resolve(getRootDirname(), 'src'),
      bfSrc: path.resolve(getRootDirname(), 'blueFiberSrc'),
    },
    /*
    alias: {
      '@material-ui/styles': path.resolve(getRootDirname(), 'node_modules', '@material-ui/styles'),
    }, */
  },
};
