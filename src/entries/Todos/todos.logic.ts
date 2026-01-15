import { getGlobals } from '../../GLOBALS';
import { createTodosAction, deleteTodoByIdAction } from './store/todos.slice';
import { Todo, TodoId } from './decs';
import { toast } from 'react-toastify';

export const createTodos = (todos: Todo[]) => {
  getGlobals().dispatch(createTodosAction({ todos }));
};

export const deleteTodoById = (id: TodoId) => {
  getGlobals().dispatch(deleteTodoByIdAction({ id })); // Assuming deleteTodoAction is defined somewhere
};

export const initTodosLogic = () => {
  // logic related to todos (websocket, fetch data, ...)

  fetch('assets/demoTodos.json')
    .then((data) => data.json())
    .then((response) => {
      const todos = response.todos;
      createTodos(todos);
    })
    .catch((error) => {
      toast.error(`Error fetching todos:  ${error}`);
    });
};

export const destroyTodosLogic = () => {
  // logic related to todos
};
