import { configureStore, combineReducers, Reducer, EnhancedStore } from '@reduxjs/toolkit';

const defaultReducers = {};

let appReducer: Reducer | null = null;
let store: EnhancedStore | null = null;
let userReducers = { ...defaultReducers };

const rootReducer = (state, action) => {
  if (action.type === 'USER_LOGOUT') {
    state = undefined;
  }

  return appReducer?.(state, action);
};

const _configureStore = (initialStore = {}) => {
  /* eslint-disable no-underscore-dangle */
  const reduxStore = configureStore({
    reducer: rootReducer,
    preloadedState: initialStore,
    devTools: process.env.NODE_ENV !== 'production',
    /* no need for window.__REDUCE..., redux toolkit handle this */
  });
  // const reduxStore = createStore(
  //   rootReducer,
  //   window.__REDUX_DEVTOOLS_EXTENSION__ && window.__REDUX_DEVTOOLS_EXTENSION__(),
  // );

  return reduxStore;
};

/**
 * Function add a reducer to the application
 * @param {String} key The key of the reducer
 * @param {Object} reducer The reducer
 * @return {boolean} is reducer successfully added
 * @example
 * import myCustomReducer from "./reducers/myCustomReducer";
 * Store.addReducers({ myCustomReducer: myCustomReducer });
 */

export const addSingleReducer = (key: string, reducer: Reducer) => {
  if (!key || userReducers[key]) {
    return false;
  }

  userReducers[key] = reducer;
  // @ts-ignore
  appReducer = combineReducers(userReducers);
  // @ts-ignore
  store?.replaceReducer(appReducer);

  return true;
};

export const removeSingleReducer = (key) => {
  if (!key || !userReducers[key]) {
    return false;
  }
  delete userReducers[key];
  // @ts-ignore
  appReducer = combineReducers(userReducers);
  // @ts-ignore
  store?.replaceReducer(appReducer);
  return true;
};

export const _resetStore = () => {
  // initStore({});
  userReducers = { ...defaultReducers };
  // @ts-ignore
  appReducer = combineReducers(userReducers);

  store?.replaceReducer(rootReducer);
};

/**
 * Function adds reducers to the application
 * The function should be called once, before invoking startApp.
 * @param {Object} reducers An object containing the reducers of the application
 * @return {void}
 * @example
 * import myCustomReducer from "./reducers/myCustomReducer";
 * Store.addReducers({ myCustomReducer: myCustomReducer });
 */
export const addReducers = (reducers) => {
  userReducers = { ...reducers, ...defaultReducers };
  return true;
};

/**
 * Function returns the Redux store
 * @returns {ReduxStore} The redux store
 */
export const getStore = () => store;

/**
 * Function dispatches a given action to the Redux Store
 * @param {ReduxAction} action The Redux action to be dispatched
 * @return {any}
 */
export const dispatch = (action) => {
  return store?.dispatch(action);
};

/**
 * Function returns the current Redux state
 * @return {ReduxState} The Redux state
 */
export const getState = () => {
  if (store !== null) {
    return store.getState();
  }
  return null;
};

export const addDynamicReducer = ({
  reducerName,
  reducer,
  defaultStateAction,
  defaultStateParams,
}: {
  reducerName: string;
  reducer: Reducer;
  defaultStateAction?: any;
  defaultStateParams?: any;
}) => {
  const isSuccess = addSingleReducer(reducerName, reducer);

  if (isSuccess && defaultStateAction && defaultStateParams) {
    dispatch(defaultStateAction(defaultStateParams));
  }
  return isSuccess;
};

export const removeDynamicReducer = ({ reducerName }) => {
  const isSuccess = removeSingleReducer(reducerName);
  return isSuccess;
};

export default {
  dispatch,
  getState,
};

/**
 * Function inits the Redux store with the reducers set with addReducer.
 * @return {ReduxStore} The redux store
 * @private
 */
export const initStore = ({ initialStore }) => {
  // @ts-ignore
  appReducer = combineReducers(userReducers);
  store = _configureStore(initialStore);

  return store;
};
