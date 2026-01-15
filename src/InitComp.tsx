/* eslint-disable react/prop-types */
import React, { useEffect } from 'react';
import { setGlobals } from './GLOBALS';

const InitComp = ({
  shared: {
    addDynamicReducer,
    removeDynamicReducer,
    dispatch,
    getStore,
    getState,
    addGlobalComponent,
    removeGlobalComponentById,
    addMFTheme,
    removeMFThemeById,
    updateMFTheme,
  },
}) => {
  useEffect(() => {
    setGlobals({
      addDynamicReducer,
      removeDynamicReducer,
      dispatch,
      getStore,
      getState,
      addGlobalComponent,
      removeGlobalComponentById,
      addMFTheme,
      removeMFThemeById,
      updateMFTheme,
    });

    return () => {};
  }, []);

  return <div />;
};
export default InitComp;
