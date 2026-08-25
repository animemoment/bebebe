#[compute]
#version 450

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

struct AgentGpuData {
    vec2 position;
    vec2 target;
    float speed;
    uint state;
    uint pad0;
    uint pad1;
};

layout(set = 0, binding = 0, std430) buffer AgentBuffer {
    AgentGpuData agents[];
};

layout(set = 0, binding = 1, std430) readonly buffer CollisionGrid {
    uint solidGrid[]; // 0 = свободно, 1 = вода/стена
};

layout(push_constant) uniform PushConstants {
    uint agentCount;
    float deltaTime;
    float reachDistance;
    float agentRadius;
    int mapWidth;
    int mapHeight;
    int tileSize;
} params;

bool isTileBlocked(float worldX, float worldY) {
    int tx = int(worldX / float(params.tileSize));
    int ty = int(worldY / float(params.tileSize));

    if (tx < 0 || ty < 0 || tx >= params.mapWidth || ty >= params.mapHeight) {
        return true;
    }

    int cellIdx = ty * params.mapWidth + tx;
    if (solidGrid[cellIdx] != 0u) {
        return true;
    }

    int tLeft  = int((worldX - params.agentRadius) / float(params.tileSize));
    int tRight = int((worldX + params.agentRadius) / float(params.tileSize));
    int tUp    = int((worldY - params.agentRadius) / float(params.tileSize));
    int tDown  = int((worldY + params.agentRadius) / float(params.tileSize));

    if (tLeft >= 0 && solidGrid[ty * params.mapWidth + tLeft] != 0u) return true;
    if (tRight < params.mapWidth && solidGrid[ty * params.mapWidth + tRight] != 0u) return true;
    if (tUp >= 0 && solidGrid[tUp * params.mapWidth + tx] != 0u) return true;
    if (tDown < params.mapHeight && solidGrid[tDown * params.mapWidth + tx] != 0u) return true;

    return false;
}

void main() {
    uint id = gl_GlobalInvocationID.x;
    if (id >= params.agentCount) return;

    // 0 = Idle, 2 = Chopping, 4 = Tilling, 9 = Constructing (не требуют шага движения)
    uint state = agents[id].state;
    if (state == 0u || state == 2u || state == 4u || state == 9u) {
        return;
    }

    vec2 current = agents[id].position;
    vec2 target = agents[id].target;
    vec2 dir = target - current;
    float dist = length(dir);

    if (dist <= params.reachDistance) {
        return;
    }

    vec2 step = normalize(dir) * (agents[id].speed * params.deltaTime);
    if (length(step) > dist) step = dir;

    vec2 desiredPos = current + step;

    if (!isTileBlocked(desiredPos.x, desiredPos.y)) {
        agents[id].position = desiredPos;
    } else {
        vec2 tryX = vec2(desiredPos.x, current.y);
        bool canX = !isTileBlocked(tryX.x, tryX.y);

        vec2 tryY = vec2(current.x, desiredPos.y);
        bool canY = !isTileBlocked(tryY.x, tryY.y);

        if (canX && canY) {
            float dX = dot(target - tryX, target - tryX);
            float dY = dot(target - tryY, target - tryY);
            agents[id].position = dX < dY ? tryX : tryY;
        } else if (canX) {
            agents[id].position = tryX;
        } else if (canY) {
            agents[id].position = tryY;
        }
    }
}