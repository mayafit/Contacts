import React from 'react';
import { createRoot } from 'react-dom/client';

import { ToastContainer as ToastMsgContainer } from 'react-toastify';
import { Provider } from 'react-redux';
import { createTheme, ThemeProvider, styled } from '@mui/material/styles';
import { initStore } from './configureStore';
import PluginWrapper from './PluginWrapper';
import ThemeOptions from '../MuiTheme';

const ToastContainer = styled(ToastMsgContainer)({
  fontSize: '160%',
});

const store = initStore({ initialStore: {} });
const theme = createTheme(ThemeOptions);
const jsx = (
  <Provider store={store}>
    <>
      <ThemeProvider theme={theme}>
        <PluginWrapper />
      </ThemeProvider>
      <ToastContainer />
    </>
  </Provider>
);
const container = document.getElementById('root');
const root = createRoot(container);
root.render(jsx);

document.querySelector('body').style.margin = 0;
