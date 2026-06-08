/**
 * RomangFit - Firebase Interop Helper
 */
window.firebaseHelper = {
    db: null,
    auth: null,

    initialize: function (configJson) {
        try {
            const config = JSON.parse(configJson);
            if (!config || !config.apiKey || !config.projectId) {
                console.error("Firebase config is invalid");
                return false;
            }
            
            // 만약 이미 초기화된 앱이 있으면 재사용
            if (firebase.apps.length === 0) {
                firebase.initializeApp(config);
            }
            
            this.db = firebase.firestore();
            this.auth = firebase.auth();
            return true;
        } catch (e) {
            console.error("Firebase initialization failed:", e);
            return false;
        }
    },

    signUp: async function (email, password) {
        try {
            const credential = await this.auth.createUserWithEmailAndPassword(email, password);
            return { success: true, uid: credential.user.uid, email: credential.user.email };
        } catch (e) {
            return { success: false, error: e.message };
        }
    },

    signIn: async function (email, password) {
        try {
            const credential = await this.auth.signInWithEmailAndPassword(email, password);
            return { success: true, uid: credential.user.uid, email: credential.user.email };
        } catch (e) {
            return { success: false, error: e.message };
        }
    },

    signOut: async function () {
        try {
            if (this.auth) {
                await this.auth.signOut();
                return true;
            }
            return false;
        } catch (e) {
            console.error("Sign out failed:", e);
            return false;
        }
    },

    getCurrentUser: function () {
        if (this.auth && this.auth.currentUser) {
            return { uid: this.auth.currentUser.uid, email: this.auth.currentUser.email };
        }
        return null;
    },

    setDocument: async function (collectionName, docId, dataJson) {
        try {
            if (!this.db) throw new Error("Firestore is not initialized");
            const data = JSON.parse(dataJson);
            await this.db.collection(collectionName).doc(docId).set(data, { merge: true });
            return true;
        } catch (e) {
            console.error("Firestore setDocument failed:", e);
            return false;
        }
    },

    deleteDocument: async function (collectionName, docId) {
        try {
            if (!this.db) throw new Error("Firestore is not initialized");
            await this.db.collection(collectionName).doc(docId).delete();
            return true;
        } catch (e) {
            console.error("Firestore deleteDocument failed:", e);
            return false;
        }
    },

    getDocuments: async function (collectionName) {
        try {
            if (!this.db) throw new Error("Firestore is not initialized");
            const snapshot = await this.db.collection(collectionName).get();
            const docs = [];
            snapshot.forEach(doc => {
                docs.push({ id: doc.id, ...doc.data() });
            });
            return docs;
        } catch (e) {
            console.error("Firestore getDocuments failed:", e);
            return null;
        }
    },

    getUserDocuments: async function (collectionName, userId) {
        try {
            if (!this.db) throw new Error("Firestore is not initialized");
            const snapshot = await this.db.collection(collectionName)
                .where("userId", "==", userId)
                .get();
            const docs = [];
            snapshot.forEach(doc => {
                docs.push({ id: doc.id, ...doc.data() });
            });
            return docs;
        } catch (e) {
            console.error("Firestore getUserDocuments failed:", e);
            return null;
        }
    },

    setDocumentsBatch: async function (collectionName, documentsJson) {
        try {
            if (!this.db) throw new Error("Firestore is not initialized");
            const documents = JSON.parse(documentsJson);
            
            const chunks = [];
            for (let i = 0; i < documents.length; i += 500) {
                chunks.push(documents.slice(i, i + 500));
            }
            
            for (const chunk of chunks) {
                const batch = this.db.batch();
                chunk.forEach(doc => {
                    const ref = this.db.collection(collectionName).doc(doc.id);
                    batch.set(ref, doc.data, { merge: true });
                });
                await batch.commit();
            }
            return true;
        } catch (e) {
            console.error("Firestore setDocumentsBatch failed:", e);
            return false;
        }
    }
};
