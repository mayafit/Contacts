import React, { useEffect } from 'react';
import {
  AppBar as AppBarMaterialUi,
  Toolbar,
  Typography,
  IconButton,
  useTheme,
  Button,
} from '@mui/material';
import MenuIcon from '@mui/icons-material/MenuOutlined';
import AccountCircle from '@mui/icons-material/AccountCircle';
import { useSelector } from 'react-redux';
import { getTodosSelector } from 'src/entries/Todos/store/todos.selectors';
import TodoListItem from './entries/Todos/components/TodoListItem';
import { deleteTodoById } from './entries/Todos/todos.logic';

import { destroyTodosEntry, initTodosEntry } from './entries/Todos/todos.entry';

import './styles/main.less';

import { toast } from 'react-toastify';

const App = () => {
  const theme = useTheme();
  const todos = useSelector(getTodosSelector);

  useEffect(() => {
    // Init your app
    toast('Todos app is up and running!');
    initTodosEntry();
    return () => {
      destroyTodosEntry();
    };
  }, []);

  return (
    <div style={{ width: '100%', height: '100%' }} data-testid="main-app">
      <AppBarMaterialUi position="static">
        <Toolbar
          disableGutters
          style={{
            display: 'flex',
            justifyContent: 'center',
            alignContent: 'center',
            background: theme.palette.primary.main,
          }}
        >
          <div
            style={{
              display: 'flex',
              justifyContent: 'center',
              alignItems: 'center',
            }}
          >
            <img src={`assets/images/logo-blue.png`} style={{ margin: '0 4px', height: '35px' }} />
            <Typography variant="h5" sx={{ flexGrow: 1 }}>
              Todos App
            </Typography>
          </div>
          <IconButton style={{ position: 'absolute', insetInlineStart: '15px' }}>
            <MenuIcon />
          </IconButton>
          <IconButton style={{ position: 'absolute', insetInlineEnd: '15px' }}>
            <AccountCircle />
          </IconButton>
        </Toolbar>
      </AppBarMaterialUi>
      <Typography variant="h1">Todos</Typography>
      <Button
        variant="outlined"
        onClick={() => {
          toast.success('Create a new todo was clicked!');
        }}
      >
        Create a new Todo
      </Button>
      {todos.map((todo) => (
        <TodoListItem
          key={todo.id}
          label={todo.title}
          onDeleteClick={() => {
            deleteTodoById(todo.id);
          }}
        />
      ))}
    </div>
  );
};

export default App;
