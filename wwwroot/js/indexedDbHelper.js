const DB_NAME = 'romangfit_db';
const DB_VERSION = 1;
const STORES = ['workout_history', 'workout_routines', 'exercise_definitions'];

function openDatabase() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, DB_VERSION);

        request.onupgradeneeded = (event) => {
            const db = event.target.result;
            STORES.forEach(storeName => {
                if (!db.objectStoreNames.contains(storeName)) {
                    db.createObjectStore(storeName, { keyPath: 'id' });
                }
            });
        };

        request.onsuccess = (event) => {
            resolve(event.target.result);
        };

        request.onerror = (event) => {
            reject(`IndexedDB open error: ${event.target.error}`);
        };
    });
}

window.indexedDbHelper = {
    getAll: async function (storeName) {
        const db = await openDatabase();
        return new Promise((resolve, reject) => {
            const transaction = db.transaction(storeName, 'readonly');
            const store = transaction.objectStore(storeName);
            const request = store.getAll();

            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
    },

    put: async function (storeName, item) {
        const db = await openDatabase();
        return new Promise((resolve, reject) => {
            const transaction = db.transaction(storeName, 'readwrite');
            const store = transaction.objectStore(storeName);
            // Ensure ID is present
            if (!item.id) {
                reject("Item must have an id property");
                return;
            }
            const request = store.put(item);

            request.onsuccess = () => resolve(true);
            request.onerror = () => reject(request.error);
        });
    },

    putBatch: async function (storeName, items) {
        const db = await openDatabase();
        return new Promise((resolve, reject) => {
            const transaction = db.transaction(storeName, 'readwrite');
            const store = transaction.objectStore(storeName);
            
            transaction.oncomplete = () => resolve(true);
            transaction.onerror = () => reject(transaction.error);

            items.forEach(item => {
                if (item.id) {
                    store.put(item);
                }
            });
        });
    },

    delete: async function (storeName, id) {
        const db = await openDatabase();
        return new Promise((resolve, reject) => {
            const transaction = db.transaction(storeName, 'readwrite');
            const store = transaction.objectStore(storeName);
            const request = store.delete(id);

            request.onsuccess = () => resolve(true);
            request.onerror = () => reject(request.error);
        });
    },

    clear: async function (storeName) {
        const db = await openDatabase();
        return new Promise((resolve, reject) => {
            const transaction = db.transaction(storeName, 'readwrite');
            const store = transaction.objectStore(storeName);
            const request = store.clear();

            request.onsuccess = () => resolve(true);
            request.onerror = () => reject(request.error);
        });
    }
};
