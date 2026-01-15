/* eslint-disable import/no-extraneous-dependencies */
const { merge } = require('webpack-merge');
const { CleanWebpackPlugin } = require('clean-webpack-plugin');
const ForkTsCheckerWebpackPlugin = require('fork-ts-checker-webpack-plugin');
const CompressionPlugin = require('compression-webpack-plugin');
const path = require('path');
const zlib = require('zlib');
const BFConfig = require('../bluefiber.config');
// const BrotliPlugin = require('brotli-webpack-plugin');
const TerserPlugin = require('terser-webpack-plugin');

const common = require('./_webpack.bf.common');

const appCommon = require('../webpack.common');
const appProd = require('../webpack.prod');
const featuresFlags = require('../features-flags-prod.json');

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
const getRootDirname = () => {
  return path.resolve(__dirname, '..');
};

module.exports = (env, args) => {
  return merge(
    common,
    appCommon,
    {
      mode: process.env?.DEV_BUILD === 'true' ? 'development' : 'production',

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
                  configFile: path.resolve(
                    getRootDirname(),
                    process.env?.DEV_BUILD === 'true'
                      ? './tsconfig.dev.json'
                      : './tsconfig.prod.json',
                  ),
                },
              },
              {
                loader: 'ifdef-loader',
                options: { FEATURES_FLAGS: mergedFeaturesFlag, ...BFConfig.ifdef },
              },
            ],
          },
        ],
      },

      plugins: [
        // process.env?.DEV_BUILD === 'true' ? new ForkTsCheckerWebpackPlugin() : undefined,
        new CleanWebpackPlugin({ cleanAfterEveryBuildPatterns: ['dist/*'] }),

        new CompressionPlugin({
          // filename: '[path][base].br',
          // algorithm: 'brotliCompress',
          test: /\.(js|css|html|svg)$/,
          compressionOptions: {
            // params: {
            //   [zlib.constants.BROTLI_PARAM_QUALITY]: 11,
            // },
            threshold: 8192,
            minRatio: 0.8,
          },
        }),
      ],
      devtool: process.env?.DEV_BUILD === 'true' ? 'eval-cheap-module-source-map' : undefined,
      optimization: {
        chunkIds: 'named',
        minimize: !process.env?.DEV_BUILD === 'true',
        minimizer: [
          new TerserPlugin({
            exclude: ['env.config.json'],

            terserOptions: {
              compress: {
                drop_console: !process.env?.DEV_BUILD === 'true',
              },
            },
          }),
        ],
      },
    },
    appProd,
  );
};
