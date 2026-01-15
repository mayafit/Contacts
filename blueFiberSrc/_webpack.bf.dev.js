/* eslint-disable @typescript-eslint/no-var-requires */
/* eslint-disable import/no-extraneous-dependencies */
const path = require('path');
const ReactRefreshWebpackPlugin = require('@pmmmwh/react-refresh-webpack-plugin');
const ForkTsCheckerWebpackPlugin = require('fork-ts-checker-webpack-plugin');
const ReactRefreshTypeScript = require('react-refresh-typescript');
const portFinderSync = require('portfinder-sync');
const { merge } = require('webpack-merge');
const BFConfig = require('../bluefiber.config');

const appCommon = require('../webpack.common');
const appDev = require('../webpack.dev');
const common = require('./_webpack.bf.common');

const featuresFlags = require('../features-flags-dev.json');
let envFeaturesFlag = {};
// Object.keys(featuresFlags).forEach((ff) => {
//   if (!!process.env[ff]) {
//     envFeaturesFlag[ff] = process.env[ff];
//   }
// });

const mergedFeaturesFlag = {
  ...featuresFlags,
  ...envFeaturesFlag,
};
const devPort = portFinderSync.getPort(BFConfig.devPort);

const getRootDirname = () => {
  return path.resolve(__dirname, '..');
};
module.exports = merge(
  common,
  appCommon,
  {
    mode: 'development',
    devtool: 'cheap-module-source-map',
    output: {
      chunkFilename: '[name].js',
    },
    module: {
      rules: [
        {
          test: /\.[jt]sx?$/,
          exclude: [/node_modules/, /MapManager/],

          use: [
            { loader: 'babel-loader' },
            {
              loader: require.resolve('ts-loader'),
              options: {
                configFile: path.resolve(getRootDirname(), 'tsconfig.dev.json'),
                getCustomTransformers: () => ({
                  before: [ReactRefreshTypeScript()].filter(Boolean),
                }),
              },
            },
            {
              loader: 'ifdef-loader',
              options: {
                FEATURES_FLAGS: mergedFeaturesFlag,
                ...BFConfig.ifdef,
              },
            },
          ],
        },
      ],
    },

    plugins: [
      new ReactRefreshWebpackPlugin({ overlay: BFConfig.enableDevelopmentErrorOverlay }),
      new ForkTsCheckerWebpackPlugin(),
    ].filter(Boolean),
    devServer: {
      hot: true,
      port: devPort,
      headers: {
        'Access-Control-Allow-Origin': '*',
        'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, PATCH, OPTIONS',
        'Access-Control-Allow-Headers': 'X-Requested-With, content-type, Authorization',
      },
      client: {
        overlay: BFConfig.enableDevelopmentErrorOverlay,
      },

      // open: true,
      host: '0.0.0.0',
      // contentBase: path.join(getRootDirname, 'public'),
      static: {
        publicPath: path.resolve(getRootDirname(), 'dist'),
      },
      historyApiFallback: true, // For react-router
    },
  },
  appDev,
);
