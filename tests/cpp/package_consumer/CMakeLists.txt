cmake_minimum_required(VERSION 3.20)
project(SharedMemoryStorePackageConsumer LANGUAGES CXX)

find_package(SharedMemoryStore CONFIG REQUIRED)
add_executable(shared_memory_store_package_consumer main.cpp)
target_compile_features(shared_memory_store_package_consumer PRIVATE cxx_std_20)
target_link_libraries(
    shared_memory_store_package_consumer
    PRIVATE SharedMemoryStore::SharedMemoryStore)

if(WIN32)
    add_custom_command(
        TARGET shared_memory_store_package_consumer
        POST_BUILD
        COMMAND
            "${CMAKE_COMMAND}" -E copy_if_different
            "$<TARGET_FILE:SharedMemoryStore::SharedMemoryStore>"
            "$<TARGET_FILE_DIR:shared_memory_store_package_consumer>"
        VERBATIM)
endif()
