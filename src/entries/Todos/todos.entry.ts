import { getGlobals } from '../../GLOBALS';
import todosReducer from './store/todos.slice';
import { initTodosLogic, destroyTodosLogic } from './todos.logic';

export const initTodosEntry = () => {
  getGlobals().addDynamicReducer({
    reducerName: 'todosSlice',
    reducer: todosReducer,
  });

  initTodosLogic(); //   // defaultStateParams: { newState: { isLoaded: true, newProperty: true } },
};
export const destroyTodosEntry = () => {
  destroyTodosLogic();
};
