let _globals: {
  addDynamicReducer?: any;
  dispatch?: any;
  getStore?: any;
  getState?: any;
  addGlobalComponent?: any;
  removeGlobalComponentById?: any;
} = {};

export const setGlobals = (globals) => {
  if (!globals) {
    return;
  }
  _globals = globals;
};
export const getGlobals = () => {
  return _globals;
};
