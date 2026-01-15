import React, { useState, useEffect, ComponentType } from 'react';
import { GlobalStyles } from '@mui/material';
import InitComp from '../src/InitComp';
import App from '../src/App';
import {
  addDynamicReducer,
  dispatch,
  getStore,
  getState,
  removeDynamicReducer,
} from './configureStore';
// import 'react-toastify/dist/ReactToastify.css';

const globalStyles = {
  html: {
    fontSize: '62.5%',
    fontFamily: 'Roboto, sans-serif',
    height: '100%',
    width: '100%',
  },
  body: {
    height: '100%',
    width: '100%',
  },
  '#root': {
    width: '100%',
    height: '100%',
  },
};

const _globalComponents = {};

const PluginWrapper = () => {
  const [globalComponents, setGlobalComponents] = useState<string[]>([]);

  const addGlobalComponent = ({
    id,
    component,
  }: {
    /**
     * unique identifier of the component
     */
    id: string;

    /**
     * React component to add as a global component
     */
    component: ComponentType;
  }) => {
    _globalComponents[id] = component;
    setGlobalComponents([...globalComponents.filter((c) => c !== id), id]);
  };

  const removeGlobalComponentById = (id: string) => {
    delete _globalComponents[id];
    setGlobalComponents(globalComponents.filter((c) => c !== id));
  };

  // @ts-ignore
  window.MF_VERSION = '0.0.1';

  useEffect(() => {}, []);
  return (
    <>
      {globalComponents.map((gcId) => {
        const Comp = _globalComponents[gcId];
        return <Comp key={gcId} />;
      })}
      <GlobalStyles styles={globalStyles} />

      <InitComp
        // @ts-ignore
        shared={{
          addDynamicReducer,
          removeDynamicReducer,
          addGlobalComponent,
          removeGlobalComponentById,
          addMFTheme: () => {},
          updateMFTheme: () => {},
          removeMFThemeById: () => {},
          dispatch,
          getStore,
          getState,
        }}
      />

      <App />
    </>
  );
};

export default PluginWrapper;
