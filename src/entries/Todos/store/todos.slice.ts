import { createSlice, PayloadAction } from '@reduxjs/toolkit';
import { TodoId, TodosState, type Todo } from '../decs';
import { arrayToObject } from 'src/utils/utils';

const demoTrackDefaultState: TodosState = {
  todos: {},
  selectedTodoId: undefined,
};

const todosSlice = createSlice({
  name: 'todos',
  initialState: demoTrackDefaultState,
  reducers: {
    createTodos(state: TodosState, action: PayloadAction<{ todos: Todo[] }>) {
      const todosToAdd = action.payload.todos;
      const todosAsObject = arrayToObject({ arr: todosToAdd, keyPropertyPath: 'id' });
      state.todos = {
        ...state.todos,
        ...todosAsObject,
      };
    },
    deleteTodoById(state: TodosState, action: PayloadAction<{ id: TodoId }>) {
      const todoIdToDelete = action.payload.id;
      delete state.todos[todoIdToDelete];
    },
  },
});

export const { createTodos: createTodosAction, deleteTodoById: deleteTodoByIdAction } =
  todosSlice.actions;

export default todosSlice.reducer;
