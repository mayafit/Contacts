import _get from 'lodash-es/get';

interface DivABOptions {
  a: number;
  b: number;
}
export const divAB = (options: DivABOptions) => {
  const { a, b } = options;
  if (b === 0) {
    return null;
  }

  return a / b;
};

interface Dictionary<T> {
  [key: string]: T;
}

const isRequired = (paramName: string): Error => {
  throw new Error(`${paramName} is required`);
};

/**
 * Function checks wether a given parameter is defined (not `undefined` or `null`)
 *
 * @example
 *
 * console.log(isDefined(undefined)); // false
 * console.log(isDefined(null)); // false
 * console.log(isDefined(42)); // true
 *
 * @param value Any object
 * @returns Whether a given parameter is defined
 */
export const isDefined = <T>(value: T | undefined | null): value is T => {
  return typeof value !== 'undefined' && value !== null;
};

/**
 * Function converts an array into an object based on a specified property path.
 * @param param
 * @param param.arr The array to be converted
 * @param param.keyPropertyPath The property path to use as the key for the resulting object
 * @returns The resulting object
 *
 * @example
 * const array = [
 * { id: 1, name: 'John' },
 * { id: 2, name: 'Doe' },
 * ];
 * const object = arrayToObject({ arr: array, keyPropertyPath: 'id' });
 * console.log(object); // { 1: { id: 1, name: "John" }, 2: { id: 2, name: "Doe" } }
 */
export const arrayToObject = ({ arr = [] as any, keyPropertyPath = '' as string } = {}) => {
  const rv = {};
  arr.forEach((item: unknown) => {
    // rv[item[keyPropertyPath]] = item;
    const id = _get(item, keyPropertyPath);
    if (isDefined(id)) {
      rv[id] = item; // support nested keyPropertyPath
    }
  });
  return rv;
};

/**
 * Function updates an object immutably, **making it ideal for state management in reducers**.
 *
 * @param oldObject the old object that will be changed
 * @param newObject the new object that will be set into oldObject
 *
 * @example
 * setState(state, action) {
 *    const { newState } = action.payload;
 *    setImmutableObject(state, newState);
 * },
 */
export const setImmutableObject = (
  oldObject: Dictionary<unknown>,
  newObject = {} as Dictionary<unknown>,
) => {
  if (!isDefined(oldObject)) {
    isRequired('oldObject');
  }
  // find common keys and set them
  const commonKeys = Object.keys(newObject).filter((key) => {
    return key in oldObject;
  });
  commonKeys.forEach((key) => {
    oldObject[key] = newObject[key];
  });

  // find new keys
  const addKeys = Object.keys(newObject).filter((key) => {
    return !(key in oldObject);
  });

  addKeys.forEach((key) => {
    oldObject[key] = newObject[key];
  });

  // find keys to remove
  const removedKeys = Object.keys(oldObject).filter((key) => {
    return !(key in newObject);
  });
  removedKeys.forEach((key) => {
    delete oldObject[key];
  });
};

/**
 * Function updates an object immutably, **making it ideal for state management in reducers**.
 *
 * @param oldObject the old object that will be changed
 * @param newObject the new object that will be set into oldObject
 *
 * @example
 * setState(state, action) {
 *    const { newState } = action.payload;
 *    setImmutableObject(state, newState);
 * },
 */
