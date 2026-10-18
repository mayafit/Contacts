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

      // Content-hashed names: every release gets new URLs, so a browser or the
      // Cloudflare edge can never keep serving an old bundle. With fixed names
      // (main.js) and the year-long immutable caching below, users kept the
      // previous release until their cache expired. remoteEntry.js keeps its
      // fixed name (module federation contract) and is served uncached.
      output: {
        filename: '[name].[contenthash:8].js',
        chunkFilename: '[name].[contenthash:8].js',
      },

      module: {
        rules: [
          // Contacts application code - with full TypeScript type checking
          {
            test: /\.[jt]sx?$/,
            include: [path.resolve(getRootDirname(), 'src/entries/Contacts')],
            exclude: [/node_modules/],
            use: [
              { loader: 'babel-loader' },
              {
                loader: require.resolve('ts-loader'),
                options: {
                  transpileOnly: false, // Type checking enabled for Contacts application code
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
          // WebKit framework files (blueFiberSrc/ and other src/) - transpile only
          {
            test: /\.[jt]sx?$/,
            include: [
              path.resolve(getRootDirname(), 'blueFiberSrc'),
              path.resolve(getRootDirname(), 'src'),
            ],
            exclude: [
              /node_modules/,
              path.resolve(getRootDirname(), 'src/entries/Contacts'),
            ],
            use: [
              { loader: 'babel-loader' },
              {
                loader: require.resolve('ts-loader'),
                options: {
                  transpileOnly: true, // Skip type checking for WebKit framework files
                  configFile: path.resolve(getRootDirname(), './tsconfig.json'),
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
        // Was `!process.env?.DEV_BUILD === 'true'`, which is (!x) === 'true' and
        // therefore always false: production bundles shipped unminified (3.6 MB).
        minimize: process.env?.DEV_BUILD !== 'true',
        minimizer: [
          new TerserPlugin({
            exclude: ['env.config.json'],

            terserOptions: {
              compress: {
                // Kept on purpose: the app's logger writes errors to the console,
                // and those are how production problems get diagnosed.
                drop_console: false,
              },
            },
          }),
        ],
      },
    },
    appProd,
  );
};
