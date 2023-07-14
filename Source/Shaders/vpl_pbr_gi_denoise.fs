STRATUS_GLSL_VERSION

// Important Papers:
//      -> SVGF: https://research.nvidia.com/sites/default/files/pubs/2017-07_Spatiotemporal-Variance-Guided-Filtering%3A//svgf_preprint.pdf
//      -> ASVGF: https://cg.ivd.kit.edu/publications/2018/adaptive_temporal_filtering/adaptive_temporal_filtering.pdf
//      -> Q2RTX: https://developer.download.nvidia.com/video/gputechconf/gtc/2019/presentation/s91046-real-time-path-tracing-and-denoising-in-quake-2.pdf
//      -> Q2RTX + Albedo Demodulation: https://cg.informatik.uni-freiburg.de/intern/seminar/raytracing%20-%20Keller%20-%20SIGGRAPH%202019%204%20Path%20Tracing.pdf
//      -> Reconstruction Filters: https://cg.informatik.uni-freiburg.de/intern/seminar/raytracing%20-%20Keller%20-%20SIGGRAPH%202019%204%20Path%20Tracing.pdf
//      -> SVGF Presentation: https://www.highperformancegraphics.org/wp-content/uploads/2017/Papers-Session1/HPG2017_SpatiotemporalVarianceGuidedFiltering.pdf
//      -> ReSTIR: https://research.nvidia.com/sites/default/files/pubs/2020-07_Spatiotemporal-reservoir-resampling/ReSTIR.pdf
//      -> ReSTIR Math Breakdown: https://agraphicsguynotes.com/posts/understanding_the_math_behind_restir_di/
//      -> ReSTIR Theory Breakdown: https://agraphicsguynotes.com/posts/understanding_the_math_behind_restir_di/ 

#extension GL_ARB_bindless_texture : require

#include "pbr.glsl"
#include "pbr2.glsl"
#include "vpl_common.glsl"
#include "aa_common.glsl"

// Input from vertex shader
in vec2 fsTexCoords;

out vec3 combinedColor;
out vec3 giColor;
out vec4 reservoirValue;
out float newHistoryDepth;

// in/out frame texture
uniform sampler2D screen;
uniform sampler2D albedo;
uniform sampler2D velocity;
uniform sampler2D normal;
uniform sampler2D ids;
uniform sampler2D depth;
uniform sampler2DRect structureBuffer;

uniform sampler2D prevNormal;
uniform sampler2D prevIds;
uniform sampler2D prevDepth;

uniform sampler2D indirectIllumination;
uniform sampler2D indirectShadows;
uniform sampler2D prevIndirectIllumination;

uniform sampler2D originalNoisyIndirectIllumination;

uniform sampler2D historyDepth;

uniform int multiplier = 0;
uniform int passNumber = 0;
uniform bool final = false;
uniform bool mergeReservoirs = false;
uniform int numReservoirNeighbors = 20;
uniform float time;

#define COMPONENT_WISE_MIN_VALUE 0.001

// const float waveletFactors[5] = float[](
//     1.0 / 16.0, 
//     1.0 / 4.0, 
//     3.0 / 8.0, 
//     1.0 / 4.0, 
//     1.0 / 16.0
// );
// const float waveletFactors[5] = float[](
//     0.25,
//     0.5,
//     1.0,
//     0.5,
//     0.25
// );

// const float waveletFactors[3][3] = {
// 	{ 1.0  , 0.5  , 0.25  },
// 	{ 0.5  , 0.25 , 0.125 },
//     { 0.125, 0.125, 0.125 }
// };
const float waveletFactors[3][3] = {
	{ 3.0 / 8.0  , 1.0 / 4.0  , 1.0 / 16.0  },
	{ 1.0 / 4.0  , 1.0 / 16.0  , 1.0 / 16.0 },
    { 1.0 / 16.0, 1.0 / 16.0, 1.0 / 16.0 }
};

const float sigmaZ = 1.0;
const float sigmaN = 128.0;
const float sigmaL = 4.0;
const float sigmaRT = 4.0;

const int dminmax = 2;
const int dminmaxVariance = 2;

// See https://www.ncl.ac.uk/webtemplate/ask-assets/external/maths-resources/statistics/descriptive-statistics/variance-and-standard-deviation.html
float calculateLuminanceVariance(in vec2 texCoords, in int varMultiplier) {
    float average = 0.0;
    float samples = 0.0;
    float data[dminmaxVariance * dminmaxVariance + 1];
    int dataIndex = 0;
    // filter for average
    for (int dx = -dminmaxVariance; dx <= dminmaxVariance; ++dx) {
        for (int dy = -dminmaxVariance; dy <= dminmaxVariance; ++dy) {
            samples += 1.0;
            ivec2 offset = ivec2(dx, dy) + ivec2(dx, dy) * varMultiplier;

            vec3 result = textureOffset(indirectShadows, texCoords, offset).rgb;
            //result *= textureOffset(indirectIllumination, texCoords, offset).rgb;

            data[dataIndex] = linearColorToLuminance(tonemap(result));
            //data[dataIndex] = length(result);
            average += data[dataIndex];

            dataIndex += 1;
        }
    }
    average /= samples;

    // filter for sample variance
    float variance = 0.0;
    dataIndex = 0;
    for (int dx = -dminmaxVariance; dx <= dminmaxVariance; ++dx) {
        for (int dy = -dminmaxVariance; dy <= dminmaxVariance; ++dy) {
            float tmp = data[dataIndex] - average;
            variance += tmp * tmp;
            dataIndex += 1;
        }
    }

    return variance / (samples - 1.0);
}

float filterInput(
    in vec2 widthHeight,
    in vec2 texelWidthHeight,
    in vec3 centerNormal,
    in vec3 centerIllum,
    in vec3 currIllum,
    in float centerDepth,
    in float centerLum,
    in float variance,
    in int dx, in int dy,
    in int count,
    in vec2 texCoords
) {
    //if (final) return 1.0;
    if (dx == 0 && dy == 0) return 1.0;

    vec2 texelStep = (vec2(float(dx), float(dy)) + vec2(float(dx), float(dy)) * float(multiplier)) * texelWidthHeight;
    vec2 pixelCoords = texCoords * widthHeight;
    vec2 newTexCoords = texCoords + texelStep;

    float currDepth = textureOffset(depth, texCoords, ivec2(dx, dy) + ivec2(dx, dy) * multiplier).r;
    //vec2 currGradient = textureOffset(structureBuffer, texCoords * widthHeight, ivec2(dx, dy) + ivec2(dx, dy) * multiplier).xy;
    //vec2 currGradient = 1.0 - texture(structureBuffer, texCoords * widthHeight).xy;
    //currGradient = vec2(0.05);
    //float wz = exp(-abs(centerDepth - currDepth) / (sigmaZ * abs(dot(currGradient, texCoords - newTexCoords)) + 0.0001));
    float wz = exp(-abs(centerDepth - currDepth)) / sigmaZ;

    vec3 currNormal = sampleNormalWithOffset(normal, texCoords, ivec2(dx, dy) + ivec2(dx, dy) * multiplier);
    float wn = max(0.0, dot(centerNormal, currNormal));
    wn = pow(wn, sigmaN);

    // float currLum = linearColorToLuminance(tonemap(textureOffset(indirectShadows, texCoords, ivec2(dx, dy) + ivec2(dx, dy) * multiplier).rgb));
    // float lumDiff = abs(centerLum - currLum);
    // float wl = exp(-lumDiff / (sigmaL * sqrt(variance) + PREVENT_DIV_BY_ZERO));

    // float wrt = length(centerIllum - currIllum);
    // float wrt = abs(linearColorToLuminance(tonemap(centerIllum)) - linearColorToLuminance(tonemap(currIllum)));
    // float ozrt = pow(2.0, -passNumber) * sigmaRT;
    // wrt = exp(-wrt / (ozrt * ozrt));

    //float hq = waveletFactors[abs(dx)][abs(dy)];

    //return hq * wz * wn;// * wl;
    return wn * wz * 1;// * wl;// * wz * wl;// * wz * wl;
    //return wn * wz;
    //return wn * wz * wl;
}
    
vec4 computeMergedReservoir(vec3 centerNormal, float centerDepth) {
    vec3 seed = vec3(gl_FragCoord.xy, time);
    vec4 centerReservoir = texture(indirectShadows, fsTexCoords).rgba;

    int neighborhood = 10; // neighborhood X neighborhood in dimensions
    int halfNeighborhood = neighborhood / 2;
    //const int maxTries = neighborhood;

    float depthCutoff = 0.1 * centerDepth;

#define ACCEPT_OR_REJECT_RESERVOIR(minmaxOffset)                                                            \
        const int dxSign = dx_ < 0 ? -1 : 1;                                                                \
        const int dySign = dy_ < 0 ? -1 : 1;                                                                \
        dx_ += dxSign * minmaxOffset;                                                                       \
        dy_ += dySign * minmaxOffset;                                                                       \
        if (dx_ == 0 && dy_ == 0) {                                                                         \
            continue;                                                                                       \
        }                                                                                                   \
        vec3 currNormal = sampleNormalWithOffset(normal, fsTexCoords, ivec2(dx_, dy_));                     \
        /* For normalized vectors, dot(A, B) = cos(theta) where theta is the angle between them */          \
        /* If it is less than 0.906 it means the angle exceeded 25 degrees (positive or negative angle) */  \
        if (dot(centerNormal, currNormal) < 0.906) {                                                        \
            continue;                                                                                       \
        }                                                                                                   \
        float currDepth = textureOffset(depth, fsTexCoords, ivec2(dx_, dy_)).r;                             \
        /* If the difference between current and center depth exceeds 10% of center's value, reject */      \
        if (abs(currDepth - centerDepth) > depthCutoff) {                                                   \
            continue;                                                                                       \
        }                                                                                                   \
        /* Neighbor seems good - merge its reservoir into this center reservoir */                          \
        vec4 currReservoir = textureOffset(indirectShadows, fsTexCoords, ivec2(dx_, dy_)).rgba;             \
        centerReservoir += currReservoir;                                                                   \

#define ACCEPT_OR_REJECT_RESERVOIR_RANDOM(n, halfN, minmaxOffset)                                           \
        float randX = random(seed);                                                                         \
        seed.z += 10000.0;                                                                                  \
        float randY = random(seed);                                                                         \
        seed.z += 10000.0;                                                                                  \
        /* Sample within the neighborhood randomly */                                                       \
        int dx_ = int(n * randX) - halfN;                                                                   \
        int dy_ = int(n * randY) - halfN;                                                                   \
        ACCEPT_OR_REJECT_RESERVOIR(minmaxOffset)

#define ACCEPT_OR_REJECT_RESERVOIR_DETERMINISTIC(minmaxOffset)                                              \
        /* Sample within the neighborhood randomly */                                                       \
        int dx_ = dx;                                                                                       \
        int dy_ = dy;                                                                                       \
        ACCEPT_OR_REJECT_RESERVOIR(minmaxOffset)

    const int nearestNeighborMinMax = numReservoirNeighbors / 2;
    const int nearestNeighborhood = 2 * nearestNeighborMinMax + 1;
    const int halfNearestNeighborhood = nearestNeighborhood / 2;
    const int halfNumReservoirNeighbors = numReservoirNeighbors / 2;

    int minmaxNearest = 2;
    for (int dx = -minmaxNearest; dx <= minmaxNearest; ++dx) {
        for (int dy = -minmaxNearest; dy <= minmaxNearest; ++dy) {
            ACCEPT_OR_REJECT_RESERVOIR_DETERMINISTIC(0)
        }
    }

    // for (int count = 0; count < halfNumReservoirNeighbors; ++count) {
    //     ACCEPT_OR_REJECT_RESERVOIR_RANDOM(nearestNeighborhood, halfNearestNeighborhood, 0)
    // }

    for (int count = 0; count < numReservoirNeighbors; ++count) {

        ACCEPT_OR_REJECT_RESERVOIR_RANDOM(neighborhood, halfNeighborhood, minmaxNearest)

        //++count;
    }

    return centerReservoir;
}

void main() {
    vec2 widthHeight = textureSize(screen, 0);
    vec2 texelWidthHeight = 1.0 / widthHeight;
    vec3 screenColor = texture(screen, fsTexCoords).rgb;
    vec2 velocityVal = texture(velocity, fsTexCoords).xy;
    vec2 prevTexCoords = fsTexCoords - velocityVal;
    //vec3 variance = calculateVariance(fsTexCoords);
    float lumVariance = 1.0;//calculateLuminanceVariance(fsTexCoords, multiplier);
    //vec3 baseColor = texture(albedo, fsTexCoords).rgb;

    vec3 centerIllum = texture(indirectIllumination, fsTexCoords).rgb;
    float centerLum = linearColorToLuminance(centerIllum); 
    vec3 centerShadow = texture(indirectShadows, fsTexCoords).rgb;

    vec3 centerNormal = sampleNormal(normal, fsTexCoords);
    float centerDepth = texture(depth, fsTexCoords).r;

    vec3 prevCenterNormal = sampleNormal(prevNormal, prevTexCoords);
    //float prevCenterDepth = texture(depth, prevTexCoords).r;

    // vec3 centerShadow = texture(indirectShadows, fsTexCoords).rgb;
    // vec3 topShadow    = textureOffset(indirectShadows, fsTexCoords, ivec2( 0,  1)).rgb;
    // vec3 botShadow    = textureOffset(indirectShadows, fsTexCoords, ivec2( 0, -1)).rgb;
    // vec3 rightShadow  = textureOffset(indirectShadows, fsTexCoords, ivec2( 1,  0)).rgb;
    // vec3 leftShadow   = textureOffset(indirectShadows, fsTexCoords, ivec2(-1,  0)).rgb;

    vec4 reservoirFiltered = vec4(0.0);
    //vec3 shadowFactor = vec3(0.0);
    //float numShadowSamples = 0.0;
    //int filterSizeXY = 2 * dminmax + 1;
    int count = 0;
    if (mergeReservoirs) {
        reservoirFiltered = computeMergedReservoir(centerNormal, centerDepth);
    }
    else {
        for (int dx = -dminmax; dx <= dminmax; ++dx) {
            for (int dy = -dminmax; dy <= dminmax; ++dy) {
                //if (dx != 0 || dy != 0) continue;
                //if (dx == 0 && dy == 0) continue;
                ++count;
                //++numShadowSamples;
                vec4 reservoir = vec4(0.0);
                vec3 currShadow = vec3(0.0);

                reservoir = textureOffset(indirectShadows, fsTexCoords, ivec2(dx, dy) + ivec2(dx, dy) * multiplier).rgba;

                currShadow = reservoir.rgb;
                float filtered = filterInput(widthHeight, texelWidthHeight, centerNormal, centerShadow, currShadow, centerDepth, centerLum, lumVariance, dx, dy, count, fsTexCoords);
                //filtered = filtered * filtered;
                //numShadowSamples += filtered;
                reservoirFiltered += filtered * reservoir;
            }
        }
    }
    //vec3 shadowFactor = reservoirFiltered.rgb / max(PREVENT_DIV_BY_ZERO, numShadowSamples + reservoirFiltered.a);
    vec3 shadowFactor = reservoirFiltered.rgb / max(PREVENT_DIV_BY_ZERO, reservoirFiltered.a);

    //vec3 minColor = tonemap(min(centerIllum, min(topIllum, min(botIllum, min(rightIllum, leftIllum)))));
    //vec3 maxColor = tonemap(max(centerIllum, max(topIllum, max(botIllum, max(rightIllum, leftIllum)))));

    //vec3 gi = (centerIllum + topIllum + botIllum + rightIllum + leftIllum) / 5.0;
    vec3 gi = centerIllum;
    // if (final) {
    //     vec3 topIllum    = textureOffset(indirectIllumination, fsTexCoords, ivec2( 0,  1)).rgb;
    //     vec3 botIllum    = textureOffset(indirectIllumination, fsTexCoords, ivec2( 0, -1)).rgb;
    //     vec3 rightIllum  = textureOffset(indirectIllumination, fsTexCoords, ivec2( 1,  0)).rgb;
    //     vec3 leftIllum   = textureOffset(indirectIllumination, fsTexCoords, ivec2(-1,  0)).rgb;
    //     gi = (centerIllum + topIllum + botIllum + rightIllum + leftIllum) / 5.0;
    // }
    //gi = gi * shadowFactor;// * variance;
    //vec3 shadow = shadowFactor;
    //vec3 shadow = centerShadow;
    //gi = gi;
    // vec3 gi = centerIllum / (baseColor + PREVENT_DIV_BY_ZERO);
    //vec3 gi = centerIllum;

    // vec3 gi = vec3(0.0);
    // float numGiSamples = 0.0;
    // for (int dx = -1; dx <= 1; ++dx) {
    //     for (int dy = -1; dy <= 1; ++dy) {
    //         ++numGiSamples;
    //         //vec3 currAlbedo = textureOffset(albedo, fsTexCoords, ivec2(dx, dy) + ivec2(dx, dy) * multiplier).rgb;
    //         vec3 illum = textureOffset(indirectIllumination, fsTexCoords, ivec2(dx, dy) + ivec2(dx, dy) * multiplier).rgb;
    //         // currAlbedo = vec3(
    //         //     max(currAlbedo.r, COMPONENT_WISE_MIN_VALUE), 
    //         //     max(currAlbedo.g, COMPONENT_WISE_MIN_VALUE), 
    //         //     max(currAlbedo.b, COMPONENT_WISE_MIN_VALUE)
    //         // );
    //         gi += illum;
    //         //gi += vec3(luminance);
    //     }
    // }
    // gi /= numGiSamples;

    // vec3 tmPrevGi = tonemap(prevGi);
    // tmPrevGi = vec3(
    //     clamp(tmPrevGi.r, minColor.r, maxColor.r),
    //     clamp(tmPrevGi.g, minColor.g, maxColor.g),
    //     clamp(tmPrevGi.b, minColor.b, maxColor.b)
    // );
    // prevGi = inverseTonemap(tmPrevGi);

    //vec3 illumAvg = gi * shadowFactor;
    vec3 illumAvg = gi;
    float historyAccum = texture(historyDepth, fsTexCoords).r;

    if (final) {
        float accumMultiplier = 1.0;
        //vec2 currGradient = texture(structureBuffer, fsTexCoords * widthHeight).xy;
        bool complete = false;

        float similarSamples = 0.0;
        float totalSamples = 0.0;

        //vec3 currGi = gi * shadowFactor;
        vec3 currGi = shadowFactor;
        //float currLum = linearColorToLuminance(tonemap(currGi));
        //float variance = calculateLuminanceVariance(fsTexCoords, 0);

        // vec3 currColor1 = textureOffset(screen, fsTexCoords, ivec2( 0,  1)).rgb;
        // vec3 currColor2 = textureOffset(screen, fsTexCoords, ivec2( 0, -1)).rgb;
        // vec3 currColor3 = textureOffset(screen, fsTexCoords, ivec2( 1,  0)).rgb;
        // vec3 currColor4 = textureOffset(screen, fsTexCoords, ivec2(-1,  0)).rgb;

        // vec3 minColor = tonemap(min(currentColor, min(currColor1, min(currColor2, min(currColor3, currColor4)))));
        // vec3 maxColor = tonemap(max(currentColor, max(currColor1, max(currColor2, max(currColor3, currColor4)))));

        // Looks in a 3x3 temporal neighborhood to check the current sample against the geometry and lighting values
        // of the temporal neighborhood
        // for (int dx = -1; dx <= 1 && !complete; dx += 2) {
        //     for (int dy = -1; dy <= 1 && !complete; dy += 2) {
        //         ++totalSamples;

        //         float prevCenterDepth = textureOffset(depth, prevTexCoords, ivec2(dx, dy)).r;
        //         prevCenterNormal = sampleNormalWithOffset(prevNormal, prevTexCoords, ivec2(dx, dy));
        //         vec3 prevGi = textureOffset(prevIndirectIllumination, prevTexCoords, ivec2(dx, dy)).rgb;

        //         float wn = max(0.0, dot(centerNormal, prevCenterNormal));
        //         wn = pow(wn, 8.0);
        //         //if (wn < 0.95) wn = 0.0;
                
        //         //float wz = exp(-abs(centerDepth - prevCenterDepth) / (sigmaZ * abs(dot(currGradient, fsTexCoords - prevTexCoords)) + 0.0001));
        //         float wz = exp(-50.0 * abs(centerDepth - prevCenterDepth));
        //         //float wz = abs(centerDepth = prevCenterDepth);
        //         //float wz = 1.0 - abs(centerDepth - prevCenterDepth);
        //         //if (wz < 0.96) wz = 0.0;
        //         //wz = 0.0;

        //         //float wrt = length(prevGi - currGi);
        //         float wrt = abs(linearColorToLuminance(tonemap(prevGi)) - currLum);
        //         float ozrt = 5.0;//4 * exp(-variance) + 0.0001;
        //         //ozrt = 1.0 - variance + 0.0001;
        //         wrt = exp(-wrt / ozrt);
        //         //if (wrt < 0.97) wrt = 0.0;

        //         float similarity = 1 * 1 * 1;
                
        //         if (similarity > 0.95) {
        //             ++similarSamples;
        //             similarity = 0.0;
        //             //accumMultiplier = 0.0;
        //             //complete = true;
        //         }
        //     }
        // }

        // float similarity = similarSamples / totalSamples;
        // if (similarity < 0.25) {
        //     accumMultiplier = 0.0;
        // }

        float prevCenterDepth = texture(depth, prevTexCoords).r;
        prevCenterNormal = sampleNormalWithOffset(prevNormal, prevTexCoords, ivec2(0, 0));
        vec3 prevGi = textureOffset(prevIndirectIllumination, prevTexCoords, ivec2(0, 0)).rgb;

        float currId = texture(ids, fsTexCoords).r;
        float prevId = texture(prevIds, prevTexCoords).r;

        float wn = max(0.0, dot(centerNormal, prevCenterNormal));
        //wn = pow(wn, 2.0);
        //float similarity = 1.0;
        // float wn = 1.0;
        // /* For normalized vectors, dot(A, B) = cos(theta) where theta is the angle between them */ 
        // /* If it is less than 0.906 it means the angle exceeded 25 degrees (positive or negative angle) */
        // if (dot(centerNormal, prevCenterNormal) < 0.97) {                                                        
        //     wn = 0.0;                                                                                       
        // }                                                                                                   
        //if (wn < 0.95) wn = 0.0;

        // float depthCutoff = 0.01 * centerDepth;
        // float wz = 1.0;   
        // if (abs(centerDepth - prevCenterDepth) > depthCutoff) {                                                   
        //     //continue;    
        //     wz = 0.0;                                                                                   
        // }     
        float wz = exp(-50 * abs(centerDepth - prevCenterDepth));
        
        //float wz = exp(-abs(centerDepth - prevCenterDepth) / (sigmaZ * abs(dot(currGradient, fsTexCoords - prevTexCoords)) + 0.0001));                                                                                              
        // float wz = exp(-50.0 * abs(centerDepth - prevCenterDepth));
        //float wz = abs(centerDepth = prevCenterDepth);
        //float wz = 1.0 - abs(centerDepth - prevCenterDepth);
        //if (wz < 0.96) wz = 0.0;
        //wz = 0.0;

        //float wrt = length(prevGi - currGi);
        //float wrt = abs(linearColorToLuminance(tonemap(prevGi)) - currLum);
        // float ozrt = 5.0;//4 * exp(-variance) + 0.0001;
        // //ozrt = 1.0 - variance + 0.0001;
        // wrt = exp(-wrt / ozrt);
        // if (wrt < 0.97) wrt = 0.0;
        // float wrt = 1.0;
        // if (abs(linearColorToLuminance(tonemap(prevGi)) - currLum) > 0.1) {
        //     wrt = 0.0;
        // }

        float wid = currId != prevId ? 0.0 : 1.0;

        float similarity = 1 * wz * wid;
        
        if (similarity < 0.95) {
            similarity = 0.0;
            accumMultiplier = 0.0;
            //complete = true;
        }

        prevGi = texture(prevIndirectIllumination, prevTexCoords).rgb;

        historyAccum = min(1.0 + historyAccum * accumMultiplier, 30.0);

        //shadowFactor = max(shadowFactor, 0.0025);
        //illumAvg = mix(prevGi, gi * shadowFactor, 0.05);
        //illumAvg = mix(prevGi, gi * shadowFactor, 0.1);
        float maxAccumulationFactor = 1.0 / historyAccum;
        illumAvg = mix(prevGi, currGi, maxAccumulationFactor);
        //illumAvg = shadowFactor;
        //illumAvg = mix(prevGi, currGi, maxAccumulationFactor / max(maxAccumulationFactor, similarity));
        //illumAvg = gi * shadowFactor;
        //illumAvg = vec3(wz);
        //illumAvg = vec3(similarity);
        //illumAvg = vec3(variance);
        //illumAvg = shadowFactor;
    }
    //vec3 illumAvg = shadowFactor;
    //vec3 illumAvg = vec3(variance);
    // a is effectively how many frames we can accumulate. So for example 1 / 0.1 = 10,
    // 1 / 0.05 = 20, etc.
    //vec3 illumAvg = mix(prevGi, gi, 0.05);
    //vec3 illumAvg = mix(prevGi, shadowFactor, 0.05);
    //vec3 illumAvg = mix(prevGi, gi, 1.0 / 1000.0);
    //vec3 illumAvg = vec3(difference);

    combinedColor = screenColor + gi * illumAvg;
    giColor = illumAvg;
    reservoirValue = vec4(shadowFactor, 1.0);
    newHistoryDepth = historyAccum;
}

// void main() {
//     vec3 screenColor = texture(screen, fsTexCoords).rgb;
//     vec2 velocityVal = texture(velocity, fsTexCoords).xy;
//     vec2 prevTexCoords = fsTexCoords - velocityVal;
//     //vec3 baseColor = texture(albedo, fsTexCoords).rgb;

//     vec3 centerIllum = texture(indirectIllumination, fsTexCoords).rgb;
//     vec3 topIllum    = textureOffset(indirectIllumination, fsTexCoords, ivec2( 0,  1)).rgb;
//     vec3 botIllum    = textureOffset(indirectIllumination, fsTexCoords, ivec2( 0, -1)).rgb;
//     vec3 rightIllum  = textureOffset(indirectIllumination, fsTexCoords, ivec2( 1,  0)).rgb;
//     vec3 leftIllum   = textureOffset(indirectIllumination, fsTexCoords, ivec2(-1,  0)).rgb;

//     // vec3 centerShadow = texture(indirectShadows, fsTexCoords).rgb;
//     // vec3 topShadow    = textureOffset(indirectShadows, fsTexCoords, ivec2( 0,  1)).rgb;
//     // vec3 botShadow    = textureOffset(indirectShadows, fsTexCoords, ivec2( 0, -1)).rgb;
//     // vec3 rightShadow  = textureOffset(indirectShadows, fsTexCoords, ivec2( 1,  0)).rgb;
//     // vec3 leftShadow   = textureOffset(indirectShadows, fsTexCoords, ivec2(-1,  0)).rgb;

//     vec3 shadowFactor = vec3(0.0);
//     float numShadowSamples = 0.0;
//     for (int dx = -dminmax; dx <= dminmax; ++dx) {
//         for (int dy = -dminmax; dy <= dminmax; ++dy) {
//             //if (dx != 0 || dy != 0) continue;
//             ++numShadowSamples;
//             shadowFactor += textureOffset(indirectShadows, fsTexCoords, ivec2(dx, dy) + ivec2(dx, dy) * multiplier).rgb;
//         }
//     }
//     shadowFactor /= numShadowSamples;

//     //vec3 minColor = tonemap(min(centerIllum, min(topIllum, min(botIllum, min(rightIllum, leftIllum)))));
//     //vec3 maxColor = tonemap(max(centerIllum, max(topIllum, max(botIllum, max(rightIllum, leftIllum)))));

//     //vec3 gi = (centerIllum + topIllum + botIllum + rightIllum + leftIllum) / 5.0;
//     vec3 gi = centerIllum;
//     //gi = gi * shadowFactor;
//     //vec3 shadow = shadowFactor;
//     //vec3 shadow = centerShadow;
//     //gi = gi;
//     // vec3 gi = centerIllum / (baseColor + PREVENT_DIV_BY_ZERO);
//     //vec3 gi = centerIllum;

//     // vec3 gi = vec3(0.0);
//     // float numGiSamples = 0.0;
//     // for (int dx = -1; dx <= 1; ++dx) {
//     //     for (int dy = -1; dy <= 1; ++dy) {
//     //         ++numGiSamples;
//     //         //vec3 currAlbedo = textureOffset(albedo, fsTexCoords, ivec2(dx, dy) + ivec2(dx, dy) * multiplier).rgb;
//     //         vec3 illum = textureOffset(indirectIllumination, fsTexCoords, ivec2(dx, dy) + ivec2(dx, dy) * multiplier).rgb;
//     //         // currAlbedo = vec3(
//     //         //     max(currAlbedo.r, COMPONENT_WISE_MIN_VALUE), 
//     //         //     max(currAlbedo.g, COMPONENT_WISE_MIN_VALUE), 
//     //         //     max(currAlbedo.b, COMPONENT_WISE_MIN_VALUE)
//     //         // );
//     //         gi += illum;
//     //         //gi += vec3(luminance);
//     //     }
//     // }
//     // gi /= numGiSamples;

//     vec3 prevGi = texture(prevIndirectIllumination, prevTexCoords).rgb;
//     // vec3 tmPrevGi = tonemap(prevGi);
//     // tmPrevGi = vec3(
//     //     clamp(tmPrevGi.r, minColor.r, maxColor.r),
//     //     clamp(tmPrevGi.g, minColor.g, maxColor.g),
//     //     clamp(tmPrevGi.b, minColor.b, maxColor.b)
//     // );
//     // prevGi = inverseTonemap(tmPrevGi);

//     //vec3 illumAvg = centerIllum;
//     vec3 illumAvg = shadowFactor;
//     // if (final) {
//     //     illumAvg = gi * shadowFactor;
//     // }
//     //vec3 illumAvg = shadowFactor;
//     // a is effectively how many frames we can accumulate. So for example 1 / 0.1 = 10,
//     // 1 / 0.05 = 20, etc.
//     //vec3 illumAvg = mix(prevGi, shadowFactor, 0.1);
//     //vec3 illumAvg = mix(prevGi, gi, 1.0 / 1000.0);
//     //vec3 illumAvg = vec3(difference);

//     combinedColor = screenColor + illumAvg;
//     giColor = illumAvg;
//     shadowColor = shadowFactor;
// }

// void main() {
//     vec3 screenColor = texture(screen, fsTexCoords).rgb;

//     vec3 centerIllum = texture(indirectIllumination, fsTexCoords).rgb;
//     //vec3 centerIllum = texture(indirectIllumination, fsTexCoords).rgb;
//     vec3 topIllum    = textureOffset(indirectIllumination, fsTexCoords, ivec2( 0,  1)).rgb;
//     vec3 botIllum    = textureOffset(indirectIllumination, fsTexCoords, ivec2( 0, -1)).rgb;
//     vec3 rightIllum  = textureOffset(indirectIllumination, fsTexCoords, ivec2( 1,  0)).rgb;
//     vec3 leftIllum   = textureOffset(indirectIllumination, fsTexCoords, ivec2(-1,  0)).rgb;
    
//     //vec3 illumAvg = (centerIllum + topIllum + botIllum + rightIllum + leftIllum) / 6.0;
//     vec3 illumAvg = (centerIllum + topIllum + botIllum + rightIllum + leftIllum) / 5.0;

//     //vec3 illumAvg = texture(indirectIllumination, fsTexCoords).rgb;

//     combinedColor = screenColor + illumAvg;
//     giColor = illumAvg;
// }