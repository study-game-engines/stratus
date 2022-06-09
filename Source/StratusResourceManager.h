#pragma once

#include <memory>
#include "StratusCommon.h"
#include "StratusThread.h"
#include "StratusEntity.h"
#include "StratusTexture.h"
#include "StratusRenderNode.h""
#include <vector>
#include <shared_mutex>
#include <unordered_map>

namespace stratus {
    class ResourceManager {
        friend class Engine;

        ResourceManager();

        struct RawTextureData {
            TextureConfig config;
            TextureHandle handle;
            size_t sizeBytes;
            uint8_t * data;
        };

    public:
        ResourceManager(const ResourceManager&) = delete;
        ResourceManager(ResourceManager&&) = delete;
        ResourceManager& operator=(const ResourceManager&) = delete;
        ResourceManager& operator=(ResourceManager&&) = delete;

        ~ResourceManager();

        static ResourceManager * Instance() { return _instance; }

        void Update();
        Async<Entity> LoadModel(const std::string&, RenderFaceCulling defaultCullMode = RenderFaceCulling::CULLING_CCW);
        TextureHandle LoadTexture(const std::string&, const bool srgb);
        void FinalizeModelMemory(const RenderMeshPtr&);
        bool GetTexture(const TextureHandle, Async<Texture>&) const;

        // Default shapes
        EntityPtr CreateCube();
        EntityPtr CreateQuad();

    private:
        void _ClearAsyncTextureData();
        void _ClearAsyncModelData();
        void _ClearAsyncModelData(EntityPtr);

    private:
        std::unique_lock<std::shared_mutex> _LockWrite() const { return std::unique_lock<std::shared_mutex>(_mutex); }
        std::shared_lock<std::shared_mutex> _LockRead()  const { return std::shared_lock<std::shared_mutex>(_mutex); }
        EntityPtr _LoadModel(const std::string&, RenderFaceCulling);
        std::shared_ptr<RawTextureData> _LoadTexture(const std::string&, const TextureHandle, const bool srgb);
        Texture * _FinalizeTexture(const RawTextureData&);
        uint32_t _NextResourceIndex();

        void _InitCube();
        void _InitQuad();

    private:
        static ResourceManager * _instance;
        std::vector<ThreadPtr> _threads;
        uint32_t _nextResourceVector = 0;
        EntityPtr _cube;
        EntityPtr _quad;
        std::unordered_map<std::string, Async<Entity>> _loadedModels;
        std::unordered_map<std::string, Async<Entity>> _pendingFinalize;
        std::unordered_set<RenderMeshPtr> _meshFinalizeQueue;
        std::unordered_map<TextureHandle, Async<RawTextureData>> _asyncLoadedTextureData;
        std::unordered_map<TextureHandle, Async<Texture>> _loadedTextures;
        std::unordered_map<std::string, TextureHandle> _loadedTexturesByFile;
        mutable std::shared_mutex _mutex;
    };
}