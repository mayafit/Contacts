export type TodoId = string | number;

export type Todo = {
  id: TodoId;
  title: string;
  completed: boolean;
};

export type TodosState = {
  todos: { [id: TodoId]: Todo };
  selectedTodoId?: TodoId;
};
