import { createSelector } from '@reduxjs/toolkit';
import { TodosState } from '../decs';

interface State {
  todosSlice: TodosState;
}

const getTodosDictionary = (state: State) => state?.todosSlice?.todos;

export const getTodosSelector = createSelector([getTodosDictionary], (todosDictionary = {}) => {
  return Object.values(todosDictionary);
});
